using System.Security.Cryptography;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace MailViewer;

/// <summary>Gmail IMAP 증분 동기화.
/// 새 메일을 mails.sync.mbox에 추가하고 DB에 색인한다. 앱 비밀번호는 DPAPI로 암호화 저장.</summary>
public static class GmailSync
{
    public record Account(string User, string AppPassword);

    // ── 계정 설정 (meta 테이블, 비밀번호는 DPAPI CurrentUser 암호화) ──

    public static Account? LoadAccount(string dbPath)
    {
        using var conn = Open(dbPath);
        string? user = GetMeta(conn, "gmail_user");
        string? enc = GetMeta(conn, "gmail_pass_enc");
        if (user is null || enc is null) return null;
        try
        {
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(enc), null, DataProtectionScope.CurrentUser);
            return new Account(user, Encoding.UTF8.GetString(plain));
        }
        catch
        {
            return null; // 다른 PC/계정으로 복사된 DB 등 — 재설정 필요
        }
    }

    public static void SaveAccount(string dbPath, string user, string appPassword)
    {
        var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(appPassword), null, DataProtectionScope.CurrentUser);
        using var conn = Open(dbPath);
        DbSchema.SetMeta(conn, "gmail_user", user);
        DbSchema.SetMeta(conn, "gmail_pass_enc", Convert.ToBase64String(enc));
    }

    /// <summary>연결/로그인만 시도해 계정 정보를 검증.</summary>
    public static void TestConnection(string user, string appPassword)
    {
        using var client = new ImapClient();
        client.Connect("imap.gmail.com", 993, SecureSocketOptions.SslOnConnect);
        client.Authenticate(user, appPassword);
        client.Disconnect(true);
    }

    // ── 동기화 ──

    public static (int added, int skipped) Sync(string dbPath, Action<string>? progress = null)
    {
        var account = LoadAccount(dbPath) ?? throw new InvalidOperationException("Gmail 계정이 설정되지 않았습니다.");

        using var conn = Open(dbPath);
        string syncMboxPath = GetMeta(conn, "sync_mbox_path")
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "mails.sync.mbox");
        DbSchema.SetMeta(conn, "sync_mbox_path", syncMboxPath);

        long lastUid = long.TryParse(GetMeta(conn, "sync_last_uid"), out var lu) ? lu : 0;
        long uidValidity = long.TryParse(GetMeta(conn, "sync_uidvalidity"), out var uv) ? uv : -1;

        progress?.Invoke("Gmail 접속 중...");
        using var client = new ImapClient();
        client.Connect("imap.gmail.com", 993, SecureSocketOptions.SslOnConnect);
        client.Authenticate(account.User, account.AppPassword);

        // "전체보관함" (All Mail) — 받은/보낸/보관 모두 포함
        var folder = client.GetFolder(SpecialFolder.All) ?? client.Inbox;
        folder.Open(FolderAccess.ReadOnly);

        if (uidValidity != folder.UidValidity)
        {
            // 폴더가 재구성됨 — UID 기준을 리셋하고 Message-ID 중복 검사로 방어
            lastUid = 0;
            uidValidity = folder.UidValidity;
        }

        var range = new UniqueIdRange(new UniqueId((uint)Math.Min(lastUid + 1, uint.MaxValue)), UniqueId.MaxValue);
        progress?.Invoke("새 메일 확인 중...");
        var summaries = folder.Fetch(range, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
                                            MessageSummaryItems.GMailThreadId | MessageSummaryItems.GMailLabels);

        int added = 0, skipped = 0;
        bool hasFts = HasFts(conn);

        using var insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO mails (date, from_full, from_name, from_email, to_full, cc, subject,
                               thread_key, labels, body, has_html, att_count, att_names,
                               mbox_offset, mbox_length, message_id, mbox_file)
            VALUES ($date, $ffull, $fname, $femail, $to, $cc, $subject,
                    $thread, $labels, $body, $hashtml, $attcnt, $attnames, $off, $len, $msgid, 1)
            """;

        using var mboxStream = new FileStream(syncMboxPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        long maxUid = lastUid;

        foreach (var summary in summaries)
        {
            long uid = summary.UniqueId.Id;
            if (uid <= lastUid) continue; // 범위 검색이 마지막 UID 자체를 되돌려주는 경우 방어
            maxUid = Math.Max(maxUid, uid);

            // 이미 있는 메일은 본문 다운로드 없이 건너뜀 (mbox 재가져오기 후 전체 재검사 시 중요)
            string envId = summary.Envelope?.MessageId ?? "";
            if (envId.Length > 0 && MessageIdExists(conn, envId))
            {
                skipped++;
                if (skipped % 500 == 0) progress?.Invoke($"중복 확인 중... {skipped}건 건너뜀");
                continue;
            }

            var msg = folder.GetMessage(summary.UniqueId);

            string msgId = msg.MessageId ?? "";
            if (msgId.Length > 0 && MessageIdExists(conn, msgId)) { skipped++; continue; }

            // mbox 형식으로 추가 (마커 라인 + 원문 + 빈 줄)
            long offset = mboxStream.Position;
            var marker = Encoding.ASCII.GetBytes($"From MAILER-DAEMON {DateTime.UtcNow:ddd MMM d HH:mm:ss yyyy}\n");
            mboxStream.Write(marker);
            msg.WriteTo(mboxStream);
            mboxStream.Write("\n\n"u8);
            mboxStream.Flush();
            long length = mboxStream.Position - offset;

            string subject = msg.Subject ?? "";
            var (fname, femail, ffull) = MboxImporter.FormatAddress(msg.From);
            string threadKey = summary.GMailThreadId is { } thrid
                ? "gm:" + thrid
                : CsvImporter.ThreadKey(subject, (long)uid);
            string labels = summary.GMailLabels is { Count: > 0 } lbs ? string.Join(",", lbs) : "";
            string body = msg.TextBody ?? MboxImporter.StripHtml(msg.HtmlBody ?? "");
            var attNames = msg.Attachments
                .Select(a => a is MimePart mp ? mp.FileName ?? "(이름 없음)" :
                             a is MessagePart ? "(첨부 메일)" : "(기타)")
                .ToList();

            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("$date", msg.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            insert.Parameters.AddWithValue("$ffull", ffull);
            insert.Parameters.AddWithValue("$fname", fname);
            insert.Parameters.AddWithValue("$femail", femail);
            insert.Parameters.AddWithValue("$to", msg.To.ToString());
            insert.Parameters.AddWithValue("$cc", msg.Cc.ToString());
            insert.Parameters.AddWithValue("$subject", subject);
            insert.Parameters.AddWithValue("$thread", threadKey);
            insert.Parameters.AddWithValue("$labels", labels);
            insert.Parameters.AddWithValue("$body", body);
            insert.Parameters.AddWithValue("$hashtml", msg.HtmlBody is null ? 0 : 1);
            insert.Parameters.AddWithValue("$attcnt", attNames.Count);
            insert.Parameters.AddWithValue("$attnames", string.Join("; ", attNames));
            insert.Parameters.AddWithValue("$off", offset);
            insert.Parameters.AddWithValue("$len", length);
            insert.Parameters.AddWithValue("$msgid", msgId);
            insert.ExecuteNonQuery();

            if (hasFts)
            {
                // content= 외부 콘텐츠 FTS는 자동 반영이 안 되므로 수동 색인
                using var fts = conn.CreateCommand();
                fts.CommandText = """
                    INSERT INTO mails_fts(rowid, subject, from_full, to_full, cc, body)
                    SELECT id, subject, from_full, to_full, cc, body FROM mails WHERE id = last_insert_rowid()
                    """;
                fts.ExecuteNonQuery();
            }

            added++;
            if (added % 20 == 0) progress?.Invoke($"가져오는 중... {added}건");

            // UID 진행 상황을 수시로 저장해 중단돼도 이어받게
            if (added % 100 == 0)
            {
                DbSchema.SetMeta(conn, "sync_last_uid", maxUid.ToString());
                DbSchema.SetMeta(conn, "sync_uidvalidity", uidValidity.ToString());
            }
        }

        DbSchema.SetMeta(conn, "sync_last_uid", maxUid.ToString());
        DbSchema.SetMeta(conn, "sync_uidvalidity", uidValidity.ToString());
        DbSchema.SetMeta(conn, "sync_last_time", DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        client.Disconnect(true);
        return (added, skipped);
    }

    private static SqliteConnection Open(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        return conn;
    }

    private static string? GetMeta(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static bool HasFts(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='mails_fts'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private static bool MessageIdExists(SqliteConnection conn, string messageId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM mails WHERE message_id = $m LIMIT 1";
        cmd.Parameters.AddWithValue("$m", messageId);
        return cmd.ExecuteScalar() is not null;
    }
}
