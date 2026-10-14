using Microsoft.Data.Sqlite;

namespace MailViewer;

/// <summary>mails.db 스키마 v2 — mbox/CSV 임포터 공용.</summary>
public static class DbSchema
{
    public const int Version = 6;

    /// <summary>삭제 기록용 메일 식별 키: Message-ID가 있으면 그것, 없으면(CSV) 날짜|보낸이|제목.</summary>
    public const string IdentityKeyExpr =
        "CASE WHEN message_id <> '' THEN message_id ELSE date || '|' || from_email || '|' || subject END";

    public static void Create(SqliteConnection conn)
    {
        // Gmail 계정 설정(앱 비밀번호/OAuth)과 백업 폴더는 재가져오기 후에도 유지 (동기화 UID 상태는 의도적으로 리셋)
        var keep = new Dictionary<string, string>();
        try
        {
            using var read = conn.CreateCommand();
            read.CommandText = "SELECT key, value FROM meta WHERE key IN (" +
                string.Join(", ", GmailSync.AccountMetaKeys.Append("backup_dir").Select(k => $"'{k}'")) + ")";
            using var r = read.ExecuteReader();
            while (r.Read()) keep[r.GetString(0)] = r.GetString(1);
        }
        catch { /* meta 테이블 없음 (신규 DB) */ }

        // 삭제 기록(휴지통)과 별표 기록도 재가져오기 후에 유지
        var tombstones = ReadKeys(conn, "deleted_keys");
        var starKeys = ReadKeys(conn, "starred_keys");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            DROP TABLE IF EXISTS mails_fts;
            DROP TABLE IF EXISTS mails;
            DROP TABLE IF EXISTS meta;
            DROP TABLE IF EXISTS mbox_files;
            DROP TABLE IF EXISTS deleted_keys;
            CREATE TABLE deleted_keys (key TEXT PRIMARY KEY);
            DROP TABLE IF EXISTS starred_keys;
            CREATE TABLE starred_keys (key TEXT PRIMARY KEY);
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
                mbox_file   INTEGER NOT NULL DEFAULT 0, -- mbox_files.id
                deleted     INTEGER NOT NULL DEFAULT 0, -- 1=휴지통 (뷰어에서만 숨김)
                starred     INTEGER NOT NULL DEFAULT 0  -- 1=즐겨찾기 (목록 맨 위 고정)
            );
            CREATE INDEX idx_mails_date ON mails(date);
            CREATE INDEX idx_mails_from ON mails(from_name);
            CREATE INDEX idx_mails_thread ON mails(thread_key);
            CREATE INDEX idx_mails_msgid ON mails(message_id);
            CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
            -- 메일 원본 mbox 파일 목록. path는 DB 폴더 기준 상대 경로(아래에 있을 때) 또는 절대 경로
            CREATE TABLE mbox_files (id INTEGER PRIMARY KEY, path TEXT NOT NULL);
            PRAGMA user_version = {Version};
            """;
        cmd.ExecuteNonQuery();

        foreach (var (k, v) in keep) SetMeta(conn, k, v);

        WriteKeys(conn, "deleted_keys", tombstones);
        WriteKeys(conn, "starred_keys", starKeys);
    }

    private static List<string> ReadKeys(SqliteConnection conn, string table)
    {
        var keys = new List<string>();
        try
        {
            using var read = conn.CreateCommand();
            read.CommandText = $"SELECT key FROM {table}";
            using var r = read.ExecuteReader();
            while (r.Read()) keys.Add(r.GetString(0));
        }
        catch { /* 테이블 없음 (신규/구버전 DB) */ }
        return keys;
    }

    private static void WriteKeys(SqliteConnection conn, string table, List<string> keys)
    {
        if (keys.Count == 0) return;
        using var ins = conn.CreateCommand();
        ins.CommandText = $"INSERT OR IGNORE INTO {table} (key) VALUES ($k)";
        var p = ins.Parameters.Add("$k", SqliteType.Text);
        foreach (var k in keys) { p.Value = k; ins.ExecuteNonQuery(); }
    }

    /// <summary>가져오기 완료 후: 삭제/별표 기록에 있는 메일의 상태를 복원.</summary>
    public static void ApplyTombstones(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE mails SET deleted = 1 WHERE ({IdentityKeyExpr}) IN (SELECT key FROM deleted_keys);
            UPDATE mails SET starred = 1 WHERE ({IdentityKeyExpr}) IN (SELECT key FROM starred_keys);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>구버전 DB를 현재 버전까지 단계적으로 무손실 마이그레이션.</summary>
    public static void Migrate(string dbPath)
    {
        int v = GetVersion(dbPath);
        if (v < 2 || v >= Version) return; // v0/v1은 재가져오기 대상

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();

        if (v == 2)
        {
            cmd.CommandText = """
                ALTER TABLE mails ADD COLUMN message_id TEXT NOT NULL DEFAULT '';
                ALTER TABLE mails ADD COLUMN mbox_file INTEGER NOT NULL DEFAULT 0;
                CREATE INDEX idx_mails_msgid ON mails(message_id);
                PRAGMA user_version = 3;
                """;
            cmd.ExecuteNonQuery();
            v = 3;
        }
        if (v == 3)
        {
            cmd.CommandText = """
                ALTER TABLE mails ADD COLUMN deleted INTEGER NOT NULL DEFAULT 0;
                CREATE TABLE IF NOT EXISTS deleted_keys (key TEXT PRIMARY KEY);
                PRAGMA user_version = 4;
                """;
            cmd.ExecuteNonQuery();
            v = 4;
        }
        if (v == 4)
        {
            cmd.CommandText = """
                ALTER TABLE mails ADD COLUMN starred INTEGER NOT NULL DEFAULT 0;
                CREATE TABLE IF NOT EXISTS starred_keys (key TEXT PRIMARY KEY);
                PRAGMA user_version = 5;
                """;
            cmd.ExecuteNonQuery();
            v = 5;
        }
        if (v == 5)
        {
            // v5까지 mbox_file은 0=원본(meta mbox_path), 1=동기화(meta sync_mbox_path) 고정 → 같은 id로 등록하면 행은 그대로
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS mbox_files (id INTEGER PRIMARY KEY, path TEXT NOT NULL);
                INSERT OR IGNORE INTO mbox_files (id, path) SELECT 0, value FROM meta WHERE key = 'mbox_path';
                INSERT OR IGNORE INTO mbox_files (id, path) SELECT 1, value FROM meta WHERE key = 'sync_mbox_path';
                PRAGMA user_version = 6;
                """;
            cmd.ExecuteNonQuery();
        }
    }

    public static void SetMeta(SqliteConnection conn, string key, string value, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
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
