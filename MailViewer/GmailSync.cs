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
/// 새 메일을 mails.sync.mbox에 추가하고 DB에 색인한다.
/// 인증은 두 방식: 앱 비밀번호(2단계 인증 필요) 또는 Google OAuth(브라우저 로그인, 2단계 인증 불필요).
/// 비밀값(앱 비밀번호, OAuth 클라이언트 보안 비밀, 리프레시 토큰)은 모두 DPAPI CurrentUser 암호화로 meta에 저장.</summary>
public static class GmailSync
{
    /// <summary>OAuth 설정. 있으면 OAuth로, 없으면 AppPassword로 로그인.</summary>
    public record OAuthConfig(string ClientId, string ClientSecret, string RefreshToken);
    public record Account(string User, string AppPassword, OAuthConfig? OAuth = null);

    /// <summary>재가져오기 시 보존해야 하는 계정 관련 meta 키 (DbSchema.Create가 사용).</summary>
    public static readonly string[] AccountMetaKeys =
    {
        "gmail_user", "gmail_pass_enc",
        "gmail_auth", "gmail_oauth_client_id", "gmail_oauth_secret_enc", "gmail_oauth_refresh_enc",
    };

    // ── 계정 설정 (meta 테이블) ──

    public static Account? LoadAccount(string dbPath)
    {
        using var conn = Open(dbPath);
        string? user = GetMeta(conn, "gmail_user");
        if (user is null) return null;

        if (GetMeta(conn, "gmail_auth") == "oauth")
        {
            string? clientId = GetMeta(conn, "gmail_oauth_client_id");
            string? secret = Unprotect(GetMeta(conn, "gmail_oauth_secret_enc"));
            string? refresh = Unprotect(GetMeta(conn, "gmail_oauth_refresh_enc"));
            if (clientId is null || secret is null || refresh is null) return null;
            return new Account(user, "", new OAuthConfig(clientId, secret, refresh));
        }

        string? pass = Unprotect(GetMeta(conn, "gmail_pass_enc"));
        return pass is null ? null : new Account(user, pass);
    }

    /// <summary>앱 비밀번호 방식으로 저장. 기존 OAuth 클라이언트 정보는 남겨 둔다(다시 전환할 때 재입력 불필요).</summary>
    public static void SaveAccount(string dbPath, string user, string appPassword)
    {
        using var conn = Open(dbPath);
        DbSchema.SetMeta(conn, "gmail_user", user);
        DbSchema.SetMeta(conn, "gmail_pass_enc", Protect(appPassword));
        DbSchema.SetMeta(conn, "gmail_auth", "password");
    }

    /// <summary>OAuth 방식으로 저장.</summary>
    public static void SaveOAuthAccount(string dbPath, string user, OAuthConfig oauth)
    {
        using var conn = Open(dbPath);
        DbSchema.SetMeta(conn, "gmail_user", user);
        DbSchema.SetMeta(conn, "gmail_oauth_client_id", oauth.ClientId);
        DbSchema.SetMeta(conn, "gmail_oauth_secret_enc", Protect(oauth.ClientSecret));
        DbSchema.SetMeta(conn, "gmail_oauth_refresh_enc", Protect(oauth.RefreshToken));
        DbSchema.SetMeta(conn, "gmail_auth", "oauth");
    }

    /// <summary>설정 창 미리 채우기용: 저장된 OAuth 클라이언트 ID/보안 비밀 (현재 방식과 무관).</summary>
    public static (string clientId, string secret) LoadOAuthClient(string dbPath)
    {
        using var conn = Open(dbPath);
        return (GetMeta(conn, "gmail_oauth_client_id") ?? "",
                Unprotect(GetMeta(conn, "gmail_oauth_secret_enc")) ?? "");
    }

    private static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    private static string? Unprotect(string? enc)
    {
        if (enc is null) return null;
        try
        {
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(enc), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null; // 다른 PC/계정으로 복사된 DB 등 — 재설정 필요
        }
    }

    /// <summary>연결/로그인만 시도해 계정 정보를 검증.</summary>
    public static void TestConnection(string user, string appPassword)
    {
        using var client = new ImapClient();
        client.Connect("imap.gmail.com", 993, SecureSocketOptions.SslOnConnect);
        client.Authenticate(user, appPassword);
        client.Disconnect(true);
    }

    /// <summary>OAuth 액세스 토큰으로 IMAP 로그인만 시도.</summary>
    public static void TestConnectionOAuth(string user, string accessToken)
    {
        using var client = new ImapClient();
        client.Connect("imap.gmail.com", 993, SecureSocketOptions.SslOnConnect);
        client.Authenticate(new SaslMechanismOAuth2(user, accessToken));
        client.Disconnect(true);
    }

    /// <summary>계정 방식에 맞춰 로그인. OAuth는 매번 리프레시 토큰으로 새 액세스 토큰을 받는다.</summary>
    private static void Authenticate(ImapClient client, Account account)
    {
        if (account.OAuth is { } o)
        {
            string token = GoogleOAuth.GetAccessToken(o.ClientId, o.ClientSecret, o.RefreshToken);
            client.Authenticate(new SaslMechanismOAuth2(account.User, token));
        }
        else
        {
            client.Authenticate(account.User, account.AppPassword);
        }
    }

    // ── 동기화 ──

    public static (int added, int skipped) Sync(string dbPath, Action<string>? progress = null)
    {
        var account = LoadAccount(dbPath) ?? throw new InvalidOperationException(L.T("sync_no_account"));

        using var conn = Open(dbPath);
        string syncMboxPath = GetMeta(conn, "sync_mbox_path")
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "mails.sync.mbox");
        DbSchema.SetMeta(conn, "sync_mbox_path", syncMboxPath);

        long lastUid = long.TryParse(GetMeta(conn, "sync_last_uid"), out var lu) ? lu : 0;
        long uidValidity = long.TryParse(GetMeta(conn, "sync_uidvalidity"), out var uv) ? uv : -1;

        progress?.Invoke(L.T("sync_conn"));
        using var client = new ImapClient();
        client.Connect("imap.gmail.com", 993, SecureSocketOptions.SslOnConnect);
        Authenticate(client, account);

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
        progress?.Invoke(L.T("sync_check"));
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
                if (skipped % 500 == 0) progress?.Invoke(L.F("sync_dup", skipped));
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
            var attNames = MessageLoader.ExpandAttachments(msg)
                .Select(MessageLoader.AttachmentName).ToList();

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
            if (added % 20 == 0) progress?.Invoke(L.F("sync_fetch", added));

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
