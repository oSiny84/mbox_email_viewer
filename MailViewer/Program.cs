namespace MailViewer;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // EUC-KR(ks_c_5601-1987) 등 레거시 인코딩 지원 — 없으면 한국어 메일이 Latin-1로 깨져 디코딩됨
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        L.Load();

        // 헤드리스 모드들 (검증/자동화용)
        //   --import <mbox|csv>... <db>    : 가져오기 (확장자로 구분, mbox는 여러 개 가능 — 마지막 인자가 DB)
        //   --split-by-year <db>           : 메일 원본을 보관 폴더의 연도별 mbox로 정리
        //   --backup <db> [폴더]           : DB와 메일 원본을 백업 폴더로 (폴더 생략 시 GUI에서 정한 위치)
        //   --export <db> <xlsx> [검색어]  : 검색 결과 엑셀 저장
        //   --dump <db> <mail-id>          : mbox에서 즉석 파싱 결과 요약 출력
        if (args.Length >= 3 && args[0] == "--import")
        {
            int n = ImportUi.RunHeadless(args[1..^1], args[^1]);
            Console.WriteLine($"imported {n} rows -> {args[^1]}");
            return 0;
        }
        // 헤드리스 모드도 DB 인자를 받으면 구버전 마이그레이션 먼저
        if (args.Length >= 2 && args[0] is "--sync" or "--split-by-year" or "--backup" or "--export" or "--dump" or "--delete" or "--restore" or "--star" or "--unstar")
        {
            try { DbSchema.Migrate(args[1]); } catch { }
        }

        if (args.Length >= 5 && args[0] == "--diag-sync")
        {
            // 동기화 진단: --diag-sync <db> <user> <pass> <out.txt>  (암호화 저장 왕복 + IMAP 접속 확인)
            // 주의: 계정을 앱 비밀번호 방식으로 저장하므로 OAuth로 설정된 DB에 쓰면 방식이 password로 바뀜
            var lines = new List<string>();
            GmailSync.SaveAccount(args[1], args[2], args[3]);
            var acc = GmailSync.LoadAccount(args[1]);
            lines.Add($"dpapi-roundtrip: {(acc is not null && acc.User == args[2] && acc.AppPassword == args[3] ? "OK" : $"MISMATCH ({acc?.User})")}");
            try { GmailSync.TestConnection(args[2], args[3]); lines.Add("imap: AUTH OK"); }
            catch (Exception ex) { lines.Add($"imap: {ex.GetType().Name}: {ex.Message}"); }
            File.WriteAllLines(args[4], lines);
            return 0;
        }
        if (args.Length >= 4 && args[0] == "--diag-oauth")
        {
            // OAuth 진단: --diag-oauth <user> <access-token> <out.txt>  (OAuth Playground 등에서 받은 토큰으로 IMAP XOAUTH2 확인)
            string result;
            try { GmailSync.TestConnectionOAuth(args[1], args[2]); result = "imap-oauth: AUTH OK"; }
            catch (Exception ex) { result = $"imap-oauth: {ex.GetType().Name}: {ex.Message}"; }
            File.WriteAllText(args[3], result + Environment.NewLine);
            return 0;
        }
        // 헤드리스 삭제/복원/별표: --delete|--restore|--star|--unstar <db> <id,id,...>
        if (args.Length >= 3 && args[0] is "--delete" or "--restore" or "--star" or "--unstar")
        {
            var ids = args[2].Split(',').Select(long.Parse).ToList();
            if (args[0] is "--delete" or "--restore")
                MailDb.SetDeleted(args[1], ids, deleted: args[0] == "--delete");
            else
                MailDb.SetStarred(args[1], ids, starred: args[0] == "--star");
            Console.WriteLine($"{args[0]} {ids.Count} mails");
            return 0;
        }
        if (args.Length >= 2 && args[0] == "--sync")
        {
            // 예약 작업 등에서 사용: MailViewer --sync mails.db (계정은 GUI에서 미리 설정)
            var (added, skipped) = GmailSync.Sync(args[1]);
            Console.WriteLine($"sync: +{added}, dup {skipped}");
            return 0;
        }
        if (args.Length >= 2 && args[0] == "--backup")
        {
            // 작업 스케줄러용: MailViewer --backup mails.db ["D:\OneDrive\메일백업"]
            string? dir = args.Length >= 3 ? args[2] : Backup.GetTarget(args[1]);
            if (dir is null) { Console.WriteLine("backup: no target folder (set it once in the GUI or pass it)"); return 1; }
            var r = Backup.Run(args[1], dir);
            Console.WriteLine($"backup: {r.Copied} copied ({r.CopiedBytes / 1048576.0:N1} MB), {r.Unchanged} unchanged -> {r.TargetDir}");
            foreach (var f in r.Outside) Console.WriteLine($"not backed up (outside DB folder): {f}");
            return 0;
        }
        if (args.Length >= 2 && args[0] == "--split-by-year")
        {
            var r = MboxStore.SplitByYear(args[1]);
            Console.WriteLine($"split: moved {r.Moved} -> {r.ArchiveDir}");
            foreach (var f in r.UnusedFiles) Console.WriteLine($"unused: {f}");
            return 0;
        }
        if (args.Length >= 3 && args[0] == "--export")
        {
            using var db = new MailDb(args[1]);
            var mails = db.GetAllDetails(args.Length > 3 ? args[3] : "", "전체", null, null);
            ExcelExporter.Export(mails, args[2]);
            return 0;
        }
        if (args.Length >= 3 && args[0] == "--dump")
        {
            using var db = new MailDb(args[1]);
            var d = db.GetDetail(long.Parse(args[2]));
            if (d is null) { File.WriteAllText("dump.txt", "not found"); return 1; }
            string mbox = db.GetMboxPath(d.MboxFile) ?? "";
            var loaded = MessageLoader.Load(mbox, d.MboxOffset!.Value, d.MboxLength!.Value);
            var lines = new List<string>
            {
                $"subject: {loaded.Message.Subject}",
                $"from: {loaded.Message.From}",
                $"to: {loaded.Message.To}",
                $"date: {loaded.Message.Date}",
                $"html: {(loaded.Html is null ? "none" : loaded.Html.Length + " chars, cid-inlined=" + loaded.Html.Contains("data:image"))}",
                $"attachments: {loaded.Attachments.Count}",
            };
            foreach (var a in loaded.Attachments)
                lines.Add($"  - {a.Name} ({a.MimeType}, {a.Size} bytes)");
            File.WriteAllLines(args.Length > 3 ? args[3] : "dump.txt", lines);
            return 0;
        }

        ApplicationConfiguration.Initialize();

        string? dbPath = FindDb();

        // v2 이상은 무손실 마이그레이션 (컬럼 추가만)
        if (dbPath is not null)
        {
            try { DbSchema.Migrate(dbPath); } catch { }
        }

        // 그 외 구버전 스키마 DB는 다시 가져오기 필요
        if (dbPath is not null && DbSchema.GetVersion(dbPath) != DbSchema.Version)
        {
            MessageBox.Show(L.F("db_old", dbPath), L.T("db_old_title"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (!PromptImport(dbPath)) return 1;
        }
        else if (dbPath is null)
        {
            dbPath = Path.Combine(Environment.CurrentDirectory, "mails.db");
            if (!PromptImport(dbPath)) return 1;
        }

        Application.Run(new MainForm(dbPath));
        return 0;
    }

    static bool PromptImport(string dbPath)
    {
        using var dlg = new OpenFileDialog
        {
            Title = L.T("import_title"),
            Filter = L.T("import_filter"),
            Multiselect = true,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return false;

        try
        {
            int n = ImportUi.Run(null, dlg.FileNames, dbPath);
            MessageBox.Show(L.F("import_done_path", n, dbPath), L.T("import_done_title"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(L.T("import_fail") + ex.Message, L.T("err"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    /// <summary>현재 폴더 → 실행 파일 폴더 → 상위 폴더(최대 5단계) 순으로 mails.db 탐색.</summary>
    static string? FindDb()
    {
        var candidates = new List<string> { Environment.CurrentDirectory, AppContext.BaseDirectory };
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 5 && dir.Parent is not null; i++)
        {
            dir = dir.Parent;
            candidates.Add(dir.FullName);
        }
        foreach (var c in candidates)
        {
            string p = Path.Combine(c, "mails.db");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
