using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace MailViewer;

/// <summary>Gmail 백업(mbox) → SQLite. 본문 텍스트와 메타데이터만 저장하고,
/// HTML/첨부는 mbox 내 바이트 위치를 기록해 볼 때 즉석 파싱한다.
/// 여러 파일(예: 연도별 mails-YYYY.mbox)을 한 번에 가져올 수 있고, Message-ID가 같은 메일은 한 번만 넣는다.</summary>
public static class MboxImporter
{
    public static int Import(string mboxPath, string dbPath, Action<int>? progress = null) =>
        Import(new[] { mboxPath }, dbPath, progress);

    public static int Import(IReadOnlyList<string> mboxPaths, string dbPath, Action<int>? progress = null)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        DbSchema.Create(conn);
        DbSchema.SetMeta(conn, "source", "mbox");

        // 연도별 보관 파일을 가져오면 그 폴더를 보관 폴더로 (이후 동기화도 거기에 씀)
        var yearFile = mboxPaths.FirstOrDefault(MboxStore.IsYearFileName);
        if (yearFile is not null)
            DbSchema.SetMeta(conn, "archive_dir",
                MboxStore.ToStored(dbPath, Path.GetDirectoryName(Path.GetFullPath(yearFile))!));

        int count = 0;
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        using var tx = conn.BeginTransaction();
        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO mails (date, from_full, from_name, from_email, to_full, cc, subject,
                               thread_key, labels, body, has_html, att_count, att_names,
                               mbox_offset, mbox_length, message_id, mbox_file)
            VALUES ($date, $ffull, $fname, $femail, $to, $cc, $subject,
                    $thread, $labels, $body, $hashtml, $attcnt, $attnames, $off, $len, $msgid, $file)
            """;
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var name in new[] { "$date", "$ffull", "$fname", "$femail", "$to", "$cc", "$subject",
                                     "$thread", "$labels", "$body", "$hashtml", "$attcnt", "$attnames", "$off", "$len", "$msgid", "$file" })
            p[name] = insert.Parameters.Add(name, SqliteType.Text);

        foreach (var mboxPath in mboxPaths)
        {
            int fileId = MboxStore.GetOrAddFileId(conn, dbPath, mboxPath, tx);
            p["$file"].Value = fileId;
            using var stream = File.OpenRead(mboxPath);
            var parser = new MimeParser(stream, MimeFormat.Mbox);

            while (!parser.IsEndOfStream)
            {
                MimeMessage msg;
                long offset;
                try
                {
                    msg = parser.ParseMessage();
                    offset = parser.MboxMarkerOffset;
                }
                catch (Exception)
                {
                    break; // 손상된 꼬리 부분은 무시
                }
                long length = parser.Position - offset;

                // 여러 파일에 같은 메일이 있으면 처음 것만 (Message-ID 없는 메일은 비교 불가 → 모두 넣음)
                string msgId = msg.MessageId ?? "";
                if (msgId.Length > 0 && !seenIds.Add(msgId)) continue;

                string subject = msg.Subject ?? "";
                var (fname, femail, ffull) = FormatAddress(msg.From);

                string threadKey = msg.Headers["X-GM-THRID"] is { Length: > 0 } thrid
                    ? "gm:" + thrid
                    : CsvImporter.ThreadKey(subject, count + 1);

                string body = msg.TextBody ?? StripHtml(msg.HtmlBody ?? "");
                var attNames = MessageLoader.ExpandAttachments(msg)
                    .Select(MessageLoader.AttachmentName).ToList();

                p["$date"].Value = msg.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                p["$ffull"].Value = ffull;
                p["$fname"].Value = fname;
                p["$femail"].Value = femail;
                p["$to"].Value = msg.To.ToString();
                p["$cc"].Value = msg.Cc.ToString();
                p["$subject"].Value = subject;
                p["$thread"].Value = threadKey;
                p["$labels"].Value = msg.Headers["X-Gmail-Labels"] ?? "";
                p["$body"].Value = body;
                p["$hashtml"].Value = msg.HtmlBody is null ? 0 : 1;
                p["$attcnt"].Value = attNames.Count;
                p["$attnames"].Value = string.Join("; ", attNames);
                p["$off"].Value = offset;
                p["$len"].Value = length;
                p["$msgid"].Value = msgId;
                insert.ExecuteNonQuery();

                count++;
                if (count % 200 == 0) progress?.Invoke(count);
            }
        }
        tx.Commit();

        DbSchema.ApplyTombstones(conn);
        MailDb.EnsureFts(dbPath);
        return count;
    }

    internal static (string name, string email, string full) FormatAddress(InternetAddressList list)
    {
        var mb = list.Mailboxes.FirstOrDefault();
        if (mb is null) return ("", "", list.ToString());
        string name = mb.Name ?? "";
        if (name.Length == 0) name = mb.Address;
        return (name, mb.Address, list.ToString());
    }

    private static readonly Regex TagRx = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex SpaceRx = new(@"[ \t]{2,}", RegexOptions.Compiled);

    /// <summary>HTML → 검색용 대략의 텍스트.</summary>
    internal static string StripHtml(string html)
    {
        if (html.Length == 0) return "";
        string s = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", " ",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<(br|/p|/div|/tr)[^>]*>", "\n", RegexOptions.IgnoreCase);
        s = TagRx.Replace(s, " ");
        s = System.Net.WebUtility.HtmlDecode(s);
        return SpaceRx.Replace(s, " ").Trim();
    }
}
