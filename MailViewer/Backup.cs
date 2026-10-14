using Microsoft.Data.Sqlite;

namespace MailViewer;

/// <summary>백업: mails.db와 메일 원본 mbox를 지정 폴더(예: OneDrive)로 복사.
/// - DB는 SQLite 온라인 백업으로 복사 → 앱이 열려 있어도 일관된 사본.
/// - mbox는 크기·수정 시각이 바뀐 파일만 복사 (지난 연도 파일은 다시 안 올라감).
/// - 각 파일은 임시 이름으로 다 쓴 뒤 바꿔치기 → 백업 폴더엔 항상 완전한 파일만.
/// - 백업 폴더의 파일을 지우지는 않는다.
/// 복원: 백업 폴더 전체를 원하는 곳에 복사 → 그 아래에 프로그램 폴더를 두고 실행.
/// WinForms 비의존.</summary>
public static class Backup
{
    public record Result(string TargetDir, int Copied, int Unchanged, long CopiedBytes, List<string> Outside);

    public static string? GetTarget(string dbPath)
    {
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = 'backup_dir'";
        return cmd.ExecuteScalar() as string;
    }

    public static void SetTarget(string dbPath, string dir)
    {
        using var conn = Open(dbPath);
        DbSchema.SetMeta(conn, "backup_dir", Path.GetFullPath(dir));
    }

    /// <summary>백업 폴더가 DB 폴더 자체이거나 그 안이면 안 됨 (자기 자신에 복사).</summary>
    public static bool IsValidTarget(string dbPath, string dir)
    {
        string db = MboxStore.DbDir(dbPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string t = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return !t.StartsWith(db, StringComparison.OrdinalIgnoreCase);
    }

    public static Result Run(string dbPath, string targetDir, Action<string>? progress = null)
    {
        if (!IsValidTarget(dbPath, targetDir))
            throw new InvalidOperationException(L.T("backup_bad_target"));
        string dbDir = MboxStore.DbDir(dbPath);
        Directory.CreateDirectory(targetDir);

        // 백업할 mbox: 메일이 실제로 가리키는 파일 중 DB 폴더 아래에 있는 것 (상대 경로 그대로 복원 가능)
        var files = new List<(string src, string rel)>();
        var outside = new List<string>();
        using (var conn = Open(dbPath))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT path FROM mbox_files
                WHERE id IN (SELECT DISTINCT mbox_file FROM mails WHERE mbox_offset IS NOT NULL)
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string stored = r.GetString(0);
                string src = MboxStore.Resolve(dbPath, stored);
                if (Path.IsPathRooted(stored)) { outside.Add(src); continue; }
                if (File.Exists(src)) files.Add((src, stored));
            }
        }

        var toCopy = files.Where(f => Changed(f.src, Path.Combine(targetDir, f.rel))).ToList();
        long need = toCopy.Sum(f => new FileInfo(f.src).Length) + new FileInfo(dbPath).Length;
        long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(targetDir))!).AvailableFreeSpace;
        if (free < need + 50L * 1024 * 1024)
            throw new IOException(L.F("backup_no_space", need / 1048576 + 50, free / 1048576));

        // 1) DB — 온라인 백업 API (열려 있는 연결과 무관하게 일관된 사본)
        progress?.Invoke(L.T("backup_db"));
        string dbTarget = Path.Combine(targetDir, Path.GetFileName(dbPath));
        string dbTmp = dbTarget + ".tmp";
        using (var src = Open(dbPath))
        using (var dst = new SqliteConnection($"Data Source={dbTmp};Pooling=False"))
        {
            dst.Open();
            src.BackupDatabase(dst);
        }
        SqliteConnection.ClearAllPools();
        File.Move(dbTmp, dbTarget, overwrite: true);

        // 2) 바뀐 mbox만
        int i = 0;
        long bytes = 0;
        foreach (var (srcPath, rel) in toCopy)
        {
            progress?.Invoke(L.F("backup_file", ++i, toCopy.Count, rel));
            string dst = Path.Combine(targetDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            string tmp = dst + ".tmp";
            // 동기화가 쓰는 중이어도 읽을 수 있게 ReadWrite 공유
            using (var s = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20))
            using (var d = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                s.CopyTo(d);
            File.SetLastWriteTimeUtc(tmp, File.GetLastWriteTimeUtc(srcPath));
            File.Move(tmp, dst, overwrite: true);
            bytes += new FileInfo(dst).Length;
        }

        using (var conn = Open(dbPath))
            DbSchema.SetMeta(conn, "backup_last_time", DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        return new Result(Path.GetFullPath(targetDir), toCopy.Count, files.Count - toCopy.Count, bytes, outside);
    }

    /// <summary>크기나 수정 시각이 다르면 복사 대상.</summary>
    private static bool Changed(string src, string dst)
    {
        if (!File.Exists(dst)) return true;
        var a = new FileInfo(src);
        var b = new FileInfo(dst);
        return a.Length != b.Length || Math.Abs((a.LastWriteTimeUtc - b.LastWriteTimeUtc).TotalSeconds) > 2;
    }

    private static SqliteConnection Open(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        return conn;
    }
}
