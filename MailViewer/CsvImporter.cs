using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace MailViewer;

/// <summary>CSV(RFC4180, 멀티라인 필드 지원) → SQLite 가져오기.</summary>
public static class CsvImporter
{
    public static int Import(string csvPath, string dbPath, Action<int>? progress = null)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        DbSchema.Create(conn);
        DbSchema.SetMeta(conn, "source", "csv");

        int count = 0;
        using var tx = conn.BeginTransaction();
        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO mails (date, from_full, from_name, from_email, to_full, cc, subject,
                               thread_key, labels, body, has_html, att_count, att_names)
            VALUES ($date, $ffull, $fname, $femail, '', $cc, $subject, $thread, '', $body, 0, 0, '')
            """;
        var pDate = insert.Parameters.Add("$date", SqliteType.Text);
        var pFfull = insert.Parameters.Add("$ffull", SqliteType.Text);
        var pFname = insert.Parameters.Add("$fname", SqliteType.Text);
        var pFemail = insert.Parameters.Add("$femail", SqliteType.Text);
        var pSubject = insert.Parameters.Add("$subject", SqliteType.Text);
        var pThread = insert.Parameters.Add("$thread", SqliteType.Text);
        var pCc = insert.Parameters.Add("$cc", SqliteType.Text);
        var pBody = insert.Parameters.Add("$body", SqliteType.Text);

        bool isHeader = true;
        foreach (var row in ReadCsvRows(csvPath))
        {
            if (isHeader) { isHeader = false; continue; }
            if (row.Count == 0 || (row.Count == 1 && string.IsNullOrWhiteSpace(row[0]))) continue;

            string Get(int i) => i < row.Count ? row[i] : "";
            var (name, email) = ParseSender(Get(1));

            pDate.Value = NormalizeDate(Get(0));
            pFfull.Value = Get(1);
            pFname.Value = name;
            pFemail.Value = email;
            pSubject.Value = Get(2);
            pThread.Value = ThreadKey(Get(2), count + 1);
            pCc.Value = Get(4);
            pBody.Value = Get(5);
            insert.ExecuteNonQuery();

            count++;
            if (count % 500 == 0) progress?.Invoke(count);
        }
        tx.Commit();

        DbSchema.ApplyTombstones(conn);
        MailDb.EnsureFts(dbPath);
        return count;
    }

    private static readonly Regex ReplyPrefix =
        new(@"^(re|fwd|fw|답장|회신|전달)\s*:\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"Re: Fwd: 제목" → "제목". 스레드 묶기용 정규화 제목. 빈 제목은 "" 반환.</summary>
    internal static string NormalizeSubject(string subject)
    {
        string s = subject.Trim();
        while (true)
        {
            var m = ReplyPrefix.Match(s);
            if (!m.Success) break;
            s = s[m.Length..].TrimStart();
        }
        return s;
    }

    /// <summary>빈 제목 메일은 서로 무관하므로 행마다 고유한 스레드 키를 부여.</summary>
    internal static string ThreadKey(string subject, long uniqueId)
    {
        string s = NormalizeSubject(subject);
        return s.Length == 0 ? $"(제목 없음) #{uniqueId}" : s;
    }

    /// <summary>"8/30/2026" (M/D/YYYY) → "2026-08-30". 실패 시 원문 유지.</summary>
    internal static string NormalizeDate(string raw)
    {
        raw = raw.Trim();
        var parts = raw.Split('/');
        if (parts.Length == 3
            && int.TryParse(parts[0], out var m)
            && int.TryParse(parts[1], out var d)
            && int.TryParse(parts[2], out var y))
            return $"{y:D4}-{m:D2}-{d:D2}";
        if (DateTime.TryParse(raw, out var dt))
            return dt.ToString("yyyy-MM-dd");
        return raw;
    }

    /// <summary>'"이름" &lt;email&gt;' 형태에서 이름/이메일 분리.</summary>
    internal static (string name, string email) ParseSender(string raw)
    {
        raw = raw.Trim();
        string email = "";
        int lt = raw.LastIndexOf('<');
        int gt = raw.LastIndexOf('>');
        string name = raw;
        if (lt >= 0 && gt > lt)
        {
            email = raw[(lt + 1)..gt].Trim();
            name = raw[..lt].Trim();
        }
        name = name.Trim('"', ' ');
        if (name.Length == 0) name = email;
        return (name, email);
    }

    /// <summary>따옴표로 감싼 멀티라인 필드를 지원하는 스트리밍 CSV 리더.</summary>
    internal static IEnumerable<List<string>> ReadCsvRows(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        var field = new StringBuilder();
        var row = new List<string>();
        bool inQuotes = false;

        int ci;
        while ((ci = reader.Read()) >= 0)
        {
            char c = (char)ci;
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"') { field.Append('"'); reader.Read(); }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else
            {
                switch (c)
                {
                    case '"': inQuotes = true; break;
                    case ',': row.Add(field.ToString()); field.Clear(); break;
                    case '\r': break;
                    case '\n':
                        row.Add(field.ToString()); field.Clear();
                        yield return row;
                        row = new List<string>();
                        break;
                    default: field.Append(c); break;
                }
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }
}
