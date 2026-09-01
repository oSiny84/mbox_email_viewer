namespace MailViewer;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // EUC-KR(ks_c_5601-1987) 등 레거시 인코딩 지원 — 없으면 한국어 메일이 Latin-1로 깨져 디코딩됨
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        // 헤드리스 모드들 (검증/자동화용)
        //   --import <mbox|csv> <db>       : 가져오기 (확장자로 구분)
        //   --export <db> <xlsx> [검색어]  : 검색 결과 엑셀 저장
        //   --dump <db> <mail-id>          : mbox에서 즉석 파싱 결과 요약 출력
        if (args.Length >= 3 && args[0] == "--import")
        {
            int n = ImportUi.RunHeadless(args[1], args[2]);
            Console.WriteLine($"imported {n} rows -> {args[2]}");
            return 0;
        }
        // 헤드리스 모드도 DB 인자를 받으면 v2 → v3 마이그레이션 먼저
        if (args.Length >= 2 && args[0] is "--sync" or "--export" or "--dump" && DbSchema.GetVersion(args[1]) == 2)
        {
            try { DbSchema.MigrateV2ToV3(args[1]); } catch { }
        }

        if (args.Length >= 5 && args[0] == "--diag-sync")
        {
            // 동기화 진단: --diag-sync <db> <user> <pass> <out.txt>  (암호화 저장 왕복 + IMAP 접속 확인)
            var lines = new List<string>();
            GmailSync.SaveAccount(args[1], args[2], args[3]);
            var acc = GmailSync.LoadAccount(args[1]);
            lines.Add($"dpapi-roundtrip: {(acc is not null && acc.User == args[2] && acc.AppPassword == args[3] ? "OK" : $"MISMATCH ({acc?.User})")}");
            try { GmailSync.TestConnection(args[2], args[3]); lines.Add("imap: AUTH OK"); }
            catch (Exception ex) { lines.Add($"imap: {ex.GetType().Name}: {ex.Message}"); }
            File.WriteAllLines(args[4], lines);
            return 0;
        }
        if (args.Length >= 2 && args[0] == "--sync")
        {
            // 예약 작업 등에서 사용: MailViewer --sync mails.db (계정은 GUI에서 미리 설정)
            var (added, skipped) = GmailSync.Sync(args[1]);
            Console.WriteLine($"sync: +{added}, dup {skipped}");
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
            string mbox = db.GetMeta("mbox_path") ?? "";
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

        // v2 → v3는 무손실 마이그레이션 (컬럼 추가만)
        if (dbPath is not null && DbSchema.GetVersion(dbPath) == 2)
        {
            try { DbSchema.MigrateV2ToV3(dbPath); } catch { }
        }

        // 그 외 구버전 스키마 DB는 다시 가져오기 필요
        if (dbPath is not null && DbSchema.GetVersion(dbPath) != DbSchema.Version)
        {
            MessageBox.Show($"기존 DB({dbPath})가 구버전 형식입니다.\n메일 파일에서 다시 가져옵니다.",
                "DB 형식 변경", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            Title = "가져올 메일 파일 선택 (Gmail 백업 mbox 또는 CSV)",
            Filter = "메일 파일 (*.mbox;*.csv)|*.mbox;*.csv|모든 파일 (*.*)|*.*",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return false;

        try
        {
            int n = ImportUi.Run(null, dlg.FileName, dbPath);
            MessageBox.Show($"메일 {n:N0}건을 가져왔습니다.\n{dbPath}", "가져오기 완료",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("가져오기 실패:\n" + ex.Message, "오류",
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
