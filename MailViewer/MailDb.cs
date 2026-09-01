using Microsoft.Data.Sqlite;

namespace MailViewer;

public record MailRow(int No, long Id, string Date, string FromName, string FromEmail, string Subject, int AttCount);

public record MailDetail(long Id, string Date, string FromFull, string ToFull, string Cc,
                         string Subject, string Labels, string Body, bool HasHtml,
                         int AttCount, string AttNames, long? MboxOffset, long? MboxLength, int MboxFile);

public record SenderStat(string Name, string Email, int Count, string FirstDate, string LastDate);

public record ThreadRow(int No, string Key, string Subject, int Count, int Senders, string FirstDate, string LastDate);

/// <summary>mails.db(v2) 검색/조회. FTS5(trigram)가 있으면 3자 이상 검색어에 자동 사용.</summary>
public sealed class MailDb : IDisposable
{
    private const string DetailCols =
        "id, date, from_full, to_full, cc, subject, labels, body, has_html, att_count, att_names, mbox_offset, mbox_length, mbox_file";

    private readonly SqliteConnection _conn;
    private readonly bool _hasFts;

    public MailDb(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='mails_fts'";
        _hasFts = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    public bool HasFts => _hasFts;

    public void Dispose() => _conn.Dispose();

    public string? GetMeta(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>FTS5 trigram 인덱스가 없으면 생성. 실패 시 false — LIKE 검색으로 동작.</summary>
    public static bool EnsureFts(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='mails_fts'";
        if (Convert.ToInt32(cmd.ExecuteScalar()) > 0) return true;
        try
        {
            cmd.CommandText = """
                CREATE VIRTUAL TABLE mails_fts USING fts5(
                    subject, from_full, to_full, cc, body,
                    content='mails', content_rowid='id', tokenize='trigram'
                );
                INSERT INTO mails_fts(mails_fts) VALUES('rebuild');
                """;
            cmd.ExecuteNonQuery();
            return true;
        }
        catch
        {
            try { cmd.CommandText = "DROP TABLE IF EXISTS mails_fts"; cmd.ExecuteNonQuery(); } catch { }
            return false;
        }
    }

    /// <summary>검색 조건을 WHERE 절로 구성. 3자 이상 검색어는 FTS5 MATCH, 나머지는 LIKE.</summary>
    private string BuildWhere(SqliteCommand cmd, string keyword, string field, DateTime? from, DateTime? to,
                              bool trash = false)
    {
        var where = new List<string> { trash ? "deleted = 1" : "deleted = 0" };
        var terms = (keyword ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // trigram 토크나이저는 3자 미만 구문을 매칭하지 못하므로 짧은 검색어는 LIKE로 처리
        var ftsTerms = _hasFts ? terms.Where(t => t.Length >= 3).ToArray() : Array.Empty<string>();
        var likeTerms = terms.Except(ftsTerms).ToArray();

        if (ftsTerms.Length > 0)
        {
            string col = field switch
            {
                "subject" => "subject:", "from" => "from_full:", "to" => "to_full:",
                "body" => "body:", "cc" => "cc:",
                _ => "",
            };
            string match = string.Join(" AND ", ftsTerms.Select(t => col + "\"" + t.Replace("\"", "\"\"") + "\""));
            cmd.Parameters.AddWithValue("$fts", match);
            where.Add("id IN (SELECT rowid FROM mails_fts WHERE mails_fts MATCH $fts)");
        }

        for (int i = 0; i < likeTerms.Length; i++)
        {
            string p = $"$kw{i}";
            cmd.Parameters.AddWithValue(p, $"%{EscapeLike(likeTerms[i])}%");
            string cond = field switch
            {
                "subject" => $"subject LIKE {p} ESCAPE '\\'",
                "from" => $"from_full LIKE {p} ESCAPE '\\'",
                "to" => $"to_full LIKE {p} ESCAPE '\\'",
                "body" => $"body LIKE {p} ESCAPE '\\'",
                "cc" => $"cc LIKE {p} ESCAPE '\\'",
                _ => $"(subject LIKE {p} ESCAPE '\\' OR from_full LIKE {p} ESCAPE '\\' " +
                     $"OR to_full LIKE {p} ESCAPE '\\' OR body LIKE {p} ESCAPE '\\' OR cc LIKE {p} ESCAPE '\\')",
            };
            where.Add(cond);
        }

        if (from is not null)
        {
            cmd.Parameters.AddWithValue("$from", from.Value.ToString("yyyy-MM-dd"));
            where.Add("date >= $from");
        }
        if (to is not null)
        {
            // date는 "yyyy-MM-dd HH:mm" 텍스트 — '~'(0x7E)는 숫자보다 커서 그날 전체 포함
            cmd.Parameters.AddWithValue("$to", to.Value.ToString("yyyy-MM-dd") + " ~");
            where.Add("date <= $to");
        }

        return where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
    }

    /// <param name="field">all | subject | from | to | body | cc</param>
    public (List<MailRow> rows, int total) Search(
        string keyword, string field, DateTime? from, DateTime? to,
        bool attachedOnly = false, bool trash = false)
    {
        using var cmd = _conn.CreateCommand();
        string whereSql = BuildWhere(cmd, keyword, field, from, to, trash);
        if (attachedOnly) whereSql += " AND att_count > 0";

        cmd.CommandText = $"SELECT COUNT(*) FROM mails {whereSql}";
        int total = Convert.ToInt32(cmd.ExecuteScalar());

        cmd.CommandText = $"""
            SELECT id, date, from_name, from_email, subject, att_count
            FROM mails {whereSql}
            ORDER BY date DESC, id DESC
            """;
        var rows = new List<MailRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new MailRow(rows.Count + 1, r.GetInt64(0), r.GetString(1), r.GetString(2),
                                 r.GetString(3), r.GetString(4), r.GetInt32(5)));
        return (rows, total);
    }

    /// <summary>검색 조건에 걸린 메일을 스레드 단위로 묶어 최근 순으로 반환.</summary>
    public (List<ThreadRow> rows, int total) SearchThreads(
        string keyword, string field, DateTime? from, DateTime? to,
        bool attachedOnly = false)
    {
        using var cmd = _conn.CreateCommand();
        string whereSql = BuildWhere(cmd, keyword, field, from, to);
        if (attachedOnly) whereSql += " AND att_count > 0";

        cmd.CommandText = $"SELECT COUNT(DISTINCT thread_key) FROM mails {whereSql}";
        int total = Convert.ToInt32(cmd.ExecuteScalar());

        cmd.CommandText = $"""
            SELECT m.thread_key,
                   (SELECT subject FROM mails m2 WHERE m2.thread_key = m.thread_key
                    ORDER BY date DESC, id DESC LIMIT 1),
                   COUNT(*),
                   COUNT(DISTINCT CASE WHEN from_email = '' THEN from_full ELSE from_email END),
                   MIN(date), MAX(date)
            FROM mails m {whereSql}
            GROUP BY m.thread_key
            ORDER BY MAX(date) DESC, MAX(id) DESC
            """;
        var rows = new List<ThreadRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string subject = CsvImporter.NormalizeSubject(r.GetString(1));
            if (subject.Length == 0) subject = L.T("no_subject");
            rows.Add(new ThreadRow(rows.Count + 1, r.GetString(0), subject, r.GetInt32(2), r.GetInt32(3),
                                   r.GetString(4), r.GetString(5)));
        }
        return (rows, total);
    }

    /// <summary>스레드에 속한 모든 메일을 시간순(오래된 것부터)으로 반환.</summary>
    public List<MailDetail> GetThreadMails(string threadKey)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT {DetailCols} FROM mails WHERE thread_key = $tk AND deleted = 0 ORDER BY date ASC, id ASC";
        cmd.Parameters.AddWithValue("$tk", threadKey);
        var list = new List<MailDetail>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadDetail(r));
        return list;
    }

    public string? GetThreadKeyOf(long mailId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT thread_key FROM mails WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", mailId);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>현재 검색 조건에 해당하는 전체 메일(본문 포함) — 엑셀 내보내기용.</summary>
    public List<MailDetail> GetAllDetails(string keyword, string field, DateTime? from, DateTime? to,
                                          bool attachedOnly = false)
    {
        using var cmd = _conn.CreateCommand();
        string whereSql = BuildWhere(cmd, keyword, field, from, to);
        if (attachedOnly) whereSql += " AND att_count > 0";
        cmd.CommandText = $"SELECT {DetailCols} FROM mails {whereSql} ORDER BY date DESC, id DESC";
        var list = new List<MailDetail>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadDetail(r));
        return list;
    }

    /// <summary>현재 검색 조건 내 발신자별 통계 (건수 내림차순).</summary>
    public List<SenderStat> GetSenderStats(string keyword, string field, DateTime? from, DateTime? to)
    {
        using var cmd = _conn.CreateCommand();
        string whereSql = BuildWhere(cmd, keyword, field, from, to);
        cmd.CommandText = $"""
            SELECT MAX(from_name), from_email, COUNT(*) AS cnt, MIN(date), MAX(date)
            FROM mails {whereSql}
            GROUP BY CASE WHEN from_email = '' THEN from_full ELSE from_email END
            ORDER BY cnt DESC, 1
            """;
        var list = new List<SenderStat>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new SenderStat(r.GetString(0), r.GetString(1), r.GetInt32(2),
                                    r.GetString(3), r.GetString(4)));
        return list;
    }

    public MailDetail? GetDetail(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT {DetailCols} FROM mails WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadDetail(r) : null;
    }

    private static MailDetail ReadDetail(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
            r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt32(8) != 0,
            r.GetInt32(9), r.GetString(10),
            r.IsDBNull(11) ? null : r.GetInt64(11), r.IsDBNull(12) ? null : r.GetInt64(12),
            r.GetInt32(13));

    public (string min, string max, int count) GetStats()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT IFNULL(MIN(date),''), IFNULL(MAX(date),''), COUNT(*) FROM mails WHERE deleted = 0";
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetString(0), r.GetString(1), r.GetInt32(2));
    }

    /// <summary>메일을 휴지통으로 보내거나(deleted=1) 복원(deleted=0).
    /// 삭제 기록(deleted_keys)도 함께 유지해 mbox 재가져오기 후에도 상태가 유지된다.
    /// mbox 원본이나 Gmail 계정은 건드리지 않는다.</summary>
    public static void SetDeleted(string dbPath, IReadOnlyList<long> ids, bool deleted)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var tx = conn.BeginTransaction();

        using var upd = conn.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = "UPDATE mails SET deleted = $d WHERE id = $id";
        var pD = upd.Parameters.Add("$d", SqliteType.Integer);
        var pId = upd.Parameters.Add("$id", SqliteType.Integer);
        pD.Value = deleted ? 1 : 0;

        using var key = conn.CreateCommand();
        key.Transaction = tx;
        key.CommandText = deleted
            ? $"INSERT OR IGNORE INTO deleted_keys (key) SELECT {DbSchema.IdentityKeyExpr} FROM mails WHERE id = $id"
            : $"DELETE FROM deleted_keys WHERE key = (SELECT {DbSchema.IdentityKeyExpr} FROM mails WHERE id = $id)";
        var pKid = key.Parameters.Add("$id", SqliteType.Integer);

        foreach (long id in ids)
        {
            pId.Value = id;
            upd.ExecuteNonQuery();
            pKid.Value = id;
            key.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
