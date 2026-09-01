using Microsoft.Data.Sqlite;

namespace MailViewer;

/// <summary>mails.db 스키마 v2 — mbox/CSV 임포터 공용.</summary>
public static class DbSchema
{
    public const int Version = 3;

    public static void Create(SqliteConnection conn)
    {
        // Gmail 계정 설정은 재가져오기 후에도 유지 (동기화 UID 상태는 의도적으로 리셋)
        var keep = new Dictionary<string, string>();
        try
        {
            using var read = conn.CreateCommand();
            read.CommandText = "SELECT key, value FROM meta WHERE key IN ('gmail_user', 'gmail_pass_enc')";
            using var r = read.ExecuteReader();
            while (r.Read()) keep[r.GetString(0)] = r.GetString(1);
        }
        catch { /* meta 테이블 없음 (신규 DB) */ }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            DROP TABLE IF EXISTS mails_fts;
            DROP TABLE IF EXISTS mails;
            DROP TABLE IF EXISTS meta;
            CREATE TABLE mails (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                date        TEXT,     -- "yyyy-MM-dd HH:mm" (CSV는 날짜만)
                from_full   TEXT,     -- "이름" <email>
                from_name   TEXT,
                from_email  TEXT,
                to_full     TEXT,
                cc          TEXT,
                subject     TEXT,
                thread_key  TEXT,     -- X-GM-THRID 또는 정규화 제목
                labels      TEXT,     -- X-Gmail-Labels
                body        TEXT,     -- 검색/미리보기용 텍스트
                has_html    INTEGER NOT NULL DEFAULT 0,
                att_count   INTEGER NOT NULL DEFAULT 0,
                att_names   TEXT NOT NULL DEFAULT '',
                mbox_offset INTEGER,  -- mbox 내 시작 바이트 (CSV는 NULL)
                mbox_length INTEGER,
                message_id  TEXT NOT NULL DEFAULT '',
                mbox_file   INTEGER NOT NULL DEFAULT 0  -- 0=원본 mbox, 1=동기화 mbox
            );
            CREATE INDEX idx_mails_date ON mails(date);
            CREATE INDEX idx_mails_from ON mails(from_name);
            CREATE INDEX idx_mails_thread ON mails(thread_key);
            CREATE INDEX idx_mails_msgid ON mails(message_id);
            CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
            PRAGMA user_version = {Version};
            """;
        cmd.ExecuteNonQuery();

        foreach (var (k, v) in keep) SetMeta(conn, k, v);
    }

    /// <summary>v2 DB에 v3 컬럼을 추가하는 무손실 마이그레이션.</summary>
    public static void MigrateV2ToV3(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            ALTER TABLE mails ADD COLUMN message_id TEXT NOT NULL DEFAULT '';
            ALTER TABLE mails ADD COLUMN mbox_file INTEGER NOT NULL DEFAULT 0;
            CREATE INDEX idx_mails_msgid ON mails(message_id);
            PRAGMA user_version = 3;
            """;
        cmd.ExecuteNonQuery();
    }

    public static void SetMeta(SqliteConnection conn, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ($k, $v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public static int GetVersion(string dbPath)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch
        {
            return -1;
        }
    }
}
