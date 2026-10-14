using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace MailViewer;

/// <summary>mbox 원본 파일 관리: 파일 목록(mbox_files), 연도별 보관 파일, 레코드 쓰기, 연도별 나누기.
/// - 메일 원본은 보관 폴더(기본 DB 옆 "mbox")의 mails-YYYY.mbox에 둔다. 연도 = DB date 열의 앞 4자리.
/// - 경로는 DB 폴더 아래면 상대 경로로 저장 → DB와 보관 폴더를 통째로 옮겨도 그대로 동작.
/// WinForms 비의존.</summary>
public static class MboxStore
{
    public const string DefaultArchiveDir = "mbox";
    private static readonly Regex YearFileRx = new(@"^mails-(\d{4}|unknown)\.mbox$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── 경로 ──

    public static string DbDir(string dbPath) => Path.GetDirectoryName(Path.GetFullPath(dbPath))!;

    /// <summary>DB 폴더 아래면 상대 경로, 아니면 절대 경로.</summary>
    public static string ToStored(string dbPath, string absPath)
    {
        string full = Path.GetFullPath(absPath);
        string rel = Path.GetRelativePath(DbDir(dbPath), full);
        return rel.StartsWith("..") || Path.IsPathRooted(rel) ? full : rel;
    }

    public static string Resolve(string dbPath, string stored) =>
        Path.IsPathRooted(stored) ? stored : Path.GetFullPath(Path.Combine(DbDir(dbPath), stored));

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    public static string ArchiveDir(SqliteConnection conn, string dbPath) =>
        Resolve(dbPath, GetMeta(conn, "archive_dir") ?? DefaultArchiveDir);

    public static bool IsYearFileName(string path) => YearFileRx.IsMatch(Path.GetFileName(path));

    /// <summary>DB date("yyyy-MM-dd HH:mm") → 연도 파일 경로. 이상한 연도는 mails-unknown.mbox.</summary>
    public static string YearFilePath(string archiveDir, string date)
    {
        string year = "unknown";
        if (date.Length >= 4 && int.TryParse(date[..4], NumberStyles.None, CultureInfo.InvariantCulture, out int y)
            && y >= 1970 && y <= DateTime.Now.Year + 1)
            year = y.ToString(CultureInfo.InvariantCulture);
        return Path.Combine(archiveDir, $"mails-{year}.mbox");
    }

    // ── mbox_files 테이블 ──

    public static Dictionary<int, string> LoadFiles(SqliteConnection conn, string dbPath, SqliteTransaction? tx = null)
    {
        var d = new Dictionary<int, string>();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, path FROM mbox_files";
        using var r = cmd.ExecuteReader();
        while (r.Read()) d[r.GetInt32(0)] = Resolve(dbPath, r.GetString(1));
        return d;
    }

    public static int GetOrAddFileId(SqliteConnection conn, string dbPath, string absPath, SqliteTransaction? tx = null)
    {
        foreach (var (id, p) in LoadFiles(conn, dbPath, tx))
            if (SamePath(p, absPath)) return id;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO mbox_files (path) VALUES ($p); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$p", ToStored(dbPath, absPath));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ── 레코드 만들기 ──

    /// <summary>동기화 메일을 mbox 레코드로: Gmail 스레드/라벨 헤더를 넣어(재가져오기 때 유지) "From " 줄 + mboxrd 이스케이프.</summary>
    public static byte[] BuildRecord(MimeMessage msg, string? gmThreadId, string? labels)
    {
        if (!string.IsNullOrEmpty(gmThreadId))
        {
            msg.Headers.RemoveAll("X-GM-THRID");
            msg.Headers.Add("X-GM-THRID", gmThreadId);
        }
        if (!string.IsNullOrEmpty(labels))
        {
            msg.Headers.RemoveAll("X-Gmail-Labels");
            msg.Headers.Add("X-Gmail-Labels", labels);
        }

        using var ms = new MemoryStream();
        msg.WriteTo(ms);
        var body = EscapeFromLines(ms.ToArray());

        var marker = Encoding.ASCII.GetBytes(
            $"From MAILER-DAEMON {DateTime.UtcNow.ToString("ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture)}\n");
        var rec = new byte[marker.Length + body.Length];
        marker.CopyTo(rec, 0);
        body.CopyTo(rec, marker.Length);
        return rec;
    }

    /// <summary>mboxrd: 줄 머리의 ">*From "에 '>'를 하나 더 붙인다. 안 하면 mbox로 다시 읽을 때 그 줄에서 메일이 잘린다.</summary>
    public static byte[] EscapeFromLines(byte[] src)
    {
        var dst = new List<byte>(src.Length + 64);
        bool lineStart = true;
        for (int i = 0; i < src.Length; i++)
        {
            if (lineStart)
            {
                int j = i;
                while (j < src.Length && src[j] == (byte)'>') j++;
                if (j + 5 <= src.Length && src[j] == 'F' && src[j + 1] == 'r' && src[j + 2] == 'o' && src[j + 3] == 'm' && src[j + 4] == ' ')
                    dst.Add((byte)'>');
            }
            dst.Add(src[i]);
            lineStart = src[i] == (byte)'\n';
        }
        return dst.ToArray();
    }

    /// <summary>레코드 헤더 부분에 X-GM-THRID가 있는지 (Takeout 원본은 있음, 예전 동기화 파일은 없음).</summary>
    private static bool HasGmailHeaders(byte[] rec)
    {
        int n = Math.Min(rec.Length, 256 * 1024);
        string head = Encoding.ASCII.GetString(rec, 0, n);
        int end = head.IndexOf("\n\n", StringComparison.Ordinal);
        int end2 = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (end < 0 || (end2 >= 0 && end2 < end)) end = end2;
        if (end >= 0) head = head[..end];
        return head.Contains("\nX-GM-THRID:", StringComparison.OrdinalIgnoreCase);
    }

    // ── 파일 끝에 덧붙이기 ──

    /// <summary>여러 mbox 파일에 레코드를 덧붙인다. 각 레코드는 빈 줄 뒤 줄 머리에서 시작.
    /// 시작 시점의 파일 길이를 기억해 Rollback()으로 되돌릴 수 있다.</summary>
    public sealed class Appender : IDisposable
    {
        private readonly Dictionary<string, FileStream> _streams = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _initial = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _created = new(StringComparer.OrdinalIgnoreCase);

        public (long offset, long length) Append(string path, byte[] record)
        {
            var fs = Open(path);
            EnsureSeparator(fs);
            long offset = fs.Position;
            fs.Write(record);
            return (offset, record.Length);
        }

        private FileStream Open(string path)
        {
            string full = Path.GetFullPath(path);
            if (_streams.TryGetValue(full, out var fs)) return fs;
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (!File.Exists(full)) _created.Add(full);
            fs = new FileStream(full, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            _initial[full] = fs.Length;
            fs.Seek(0, SeekOrigin.End);
            _streams[full] = fs;
            return fs;
        }

        /// <summary>비어 있지 않은 파일이면 끝이 "\n\n"이 되도록 맞춘다.</summary>
        private static void EnsureSeparator(FileStream fs)
        {
            long len = fs.Length;
            if (len == 0) return;
            var tail = new byte[Math.Min(2, len)];
            fs.Seek(len - tail.Length, SeekOrigin.Begin);
            fs.ReadExactly(tail);
            fs.Seek(0, SeekOrigin.End);
            int newlines = 0;
            for (int i = tail.Length - 1; i >= 0 && tail[i] == '\n'; i--) newlines++;
            for (; newlines < 2; newlines++) fs.WriteByte((byte)'\n');
        }

        public void Flush()
        {
            foreach (var fs in _streams.Values) fs.Flush(flushToDisk: true);
        }

        public void Rollback()
        {
            foreach (var (path, fs) in _streams)
            {
                try { fs.SetLength(_initial[path]); } catch { /* 최선 노력 */ }
            }
            Dispose();
            foreach (var path in _created)
            {
                try { File.Delete(path); } catch { /* 최선 노력 */ }
            }
        }

        public void Dispose()
        {
            foreach (var fs in _streams.Values) fs.Dispose();
            _streams.Clear();
        }
    }

    // ── 연도별 나누기 ──

    private record SplitRow(long Id, string Date, int File, long Offset, long Length, string ThreadKey, string Labels);

    public record SplitPlan(int Count, long Bytes, string ArchiveDir, List<string> MissingFiles);

    public record SplitResult(int Moved, string ArchiveDir, List<string> UnusedFiles);

    private static List<SplitRow> RowsToMove(SqliteConnection conn, string dbPath, SqliteTransaction? tx,
                                            Dictionary<int, string> files, string archiveDir)
    {
        var rows = new List<SplitRow>();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // 원본을 순차적으로 읽도록 파일·오프셋 순
        cmd.CommandText = """
            SELECT id, date, mbox_file, mbox_offset, mbox_length, thread_key, labels
            FROM mails WHERE mbox_offset IS NOT NULL AND mbox_length IS NOT NULL
            ORDER BY mbox_file, mbox_offset
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = new SplitRow(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3), r.GetInt64(4),
                                   r.IsDBNull(5) ? "" : r.GetString(5), r.IsDBNull(6) ? "" : r.GetString(6));
            // 이미 제자리(해당 연도 파일)에 있으면 건너뜀 → 다시 실행해도 안전
            if (files.TryGetValue(row.File, out var cur) && SamePath(cur, YearFilePath(archiveDir, row.Date)))
                continue;
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>나누기 전 확인용: 옮길 건수·용량, 없는 원본 파일.</summary>
    public static SplitPlan PlanSplit(string dbPath)
    {
        using var conn = Open(dbPath);
        var files = LoadFiles(conn, dbPath);
        string archiveDir = ArchiveDir(conn, dbPath);
        var rows = RowsToMove(conn, dbPath, null, files, archiveDir);
        var missing = rows.Select(r => files.GetValueOrDefault(r.File, $"#{r.File}")).Distinct()
                          .Where(p => !File.Exists(p)).ToList();
        return new SplitPlan(rows.Count, rows.Sum(r => r.Length), archiveDir, missing);
    }

    /// <summary>모든 메일 원본을 보관 폴더의 연도별 파일로 옮기고 DB 위치 정보를 갱신.
    /// 원본 파일은 건드리지 않는다. 실패하면 덧붙인 내용을 잘라내고 DB도 롤백.</summary>
    public static SplitResult SplitByYear(string dbPath, Action<int>? progress = null)
    {
        using var conn = Open(dbPath);
        string archiveDir = ArchiveDir(conn, dbPath); // 트랜잭션 전에 (진행 중엔 모든 명령에 tx 지정 필요)
        using var tx = conn.BeginTransaction();
        var files = LoadFiles(conn, dbPath, tx);
        var rows = RowsToMove(conn, dbPath, tx, files, archiveDir);
        if (rows.Count == 0) return new SplitResult(0, archiveDir, new List<string>());

        foreach (var f in rows.Select(r => r.File).Distinct())
        {
            if (!files.TryGetValue(f, out var p) || !File.Exists(p))
                throw new FileNotFoundException(L.F("split_missing", files.GetValueOrDefault(f, $"#{f}")));
        }

        Directory.CreateDirectory(archiveDir);
        long need = rows.Sum(r => r.Length);
        long free = new DriveInfo(Path.GetPathRoot(archiveDir)!).AvailableFreeSpace;
        if (free < need + 200L * 1024 * 1024)
            throw new IOException(L.F("split_no_space", need / 1048576 + 200, free / 1048576));

        var sources = new Dictionary<int, FileStream>();
        var targetIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var appender = new Appender();
        try
        {
            using var upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "UPDATE mails SET mbox_file = $f, mbox_offset = $o, mbox_length = $l WHERE id = $id";
            var pf = upd.Parameters.Add("$f", SqliteType.Integer);
            var po = upd.Parameters.Add("$o", SqliteType.Integer);
            var pl = upd.Parameters.Add("$l", SqliteType.Integer);
            var pid = upd.Parameters.Add("$id", SqliteType.Integer);

            int done = 0;
            foreach (var row in rows)
            {
                if (!sources.TryGetValue(row.File, out var src))
                {
                    src = new FileStream(files[row.File], FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
                    sources[row.File] = src;
                }
                if (row.Offset < 0 || row.Length <= 5 || row.Offset + row.Length > src.Length)
                    throw new InvalidDataException(L.F("split_bad_record", row.Id));
                var rec = new byte[row.Length];
                src.Seek(row.Offset, SeekOrigin.Begin);
                src.ReadExactly(rec);
                if (rec[0] != 'F' || rec[1] != 'r' || rec[2] != 'o' || rec[3] != 'm' || rec[4] != ' ')
                    throw new InvalidDataException(L.F("split_bad_record", row.Id));

                // 예전 동기화 파일의 메일엔 Gmail 스레드/라벨 헤더가 없다 → DB 값으로 넣어서 다시 씀.
                // Takeout 원본(헤더 있음)은 바이트 그대로 복사 (빠르고 원본 보존).
                bool hasGm = row.ThreadKey.StartsWith("gm:", StringComparison.Ordinal) || row.Labels.Length > 0;
                if (hasGm && !HasGmailHeaders(rec))
                {
                    // 예전 동기화 파일은 본문의 "From " 줄을 이스케이프하지 않았다 → mbox로 파싱하면 거기서 잘림.
                    // 마커 줄을 떼고 메시지 하나로 파싱한 뒤 BuildRecord가 제대로 이스케이프해서 다시 쓴다.
                    int nl = Array.IndexOf(rec, (byte)'\n');
                    var msg = new MimeParser(new MemoryStream(rec, nl + 1, rec.Length - nl - 1), MimeFormat.Entity).ParseMessage();
                    string? thrid = row.ThreadKey.StartsWith("gm:", StringComparison.Ordinal) ? row.ThreadKey[3..] : null;
                    rec = BuildRecord(msg, thrid, row.Labels);
                }

                string target = YearFilePath(archiveDir, row.Date);
                if (!targetIds.TryGetValue(target, out int tid))
                {
                    tid = GetOrAddFileId(conn, dbPath, target, tx);
                    targetIds[target] = tid;
                }
                var (off, len) = appender.Append(target, rec);

                pf.Value = tid; po.Value = off; pl.Value = len; pid.Value = row.Id;
                upd.ExecuteNonQuery();

                if (++done % 200 == 0) progress?.Invoke(done);
            }

            appender.Flush();
            DbSchema.SetMeta(conn, "archive_dir", ToStored(dbPath, archiveDir), tx);
            tx.Commit();
            progress?.Invoke(done);
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            appender.Rollback();
            throw;
        }
        finally
        {
            foreach (var s in sources.Values) s.Dispose();
        }

        // 더 이상 어떤 메일도 가리키지 않는 이전 파일 (사용자가 확인 후 직접 지우도록 알려만 줌)
        var unused = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT path FROM mbox_files WHERE id NOT IN (SELECT DISTINCT mbox_file FROM mails WHERE mbox_offset IS NOT NULL)";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string p = Resolve(dbPath, r.GetString(0));
                if (File.Exists(p)) unused.Add(p);
            }
        }
        return new SplitResult(rows.Count, archiveDir, unused);
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
}
