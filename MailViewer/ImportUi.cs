namespace MailViewer;

/// <summary>mbox/CSV 가져오기를 진행 표시 창과 함께 실행하는 공용 로직.</summary>
public static class ImportUi
{
    /// <summary>CSV는 한 개만, mbox는 여러 개(연도별 파일 등)를 한 번에 가져온다.</summary>
    public static int RunHeadless(IReadOnlyList<string> sources, string dbPath, Action<int>? progress = null)
    {
        if (sources.Count == 0) throw new ArgumentException("no source");
        bool IsCsv(string p) => Path.GetExtension(p).Equals(".csv", StringComparison.OrdinalIgnoreCase);
        if (sources.Any(IsCsv))
        {
            if (sources.Count > 1) throw new InvalidOperationException(L.T("import_csv_single"));
            return CsvImporter.Import(sources[0], dbPath, progress);
        }
        // 이름순(연도별 파일이면 연도순)으로 읽어 결과를 일정하게
        return MboxImporter.Import(sources.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList(), dbPath, progress);
    }

    public static int Run(IWin32Window? owner, IReadOnlyList<string> sources, string dbPath)
    {
        string title = sources.Count == 1
            ? L.F("prog_title", Path.GetFileName(sources[0]))
            : L.F("prog_title_multi", Path.GetFileName(sources[0]), sources.Count - 1);
        return RunWithProgress(owner, title, report => RunHeadless(sources, dbPath, n => report(L.F("prog_importing", n))));
    }

    /// <summary>진행 표시 창을 띄운 채 작업을 백그라운드에서 실행. 예외는 호출자에게 다시 던진다.</summary>
    public static T RunWithProgress<T>(IWin32Window? owner, string title, Func<Action<string>, T> work)
    {
        using var pf = new ProgressForm(title);
        var task = Task.Run(() => work(pf.ReportText));
        task.ContinueWith(_ => pf.CloseSafe());
        pf.ShowDialog(owner);

        if (task.IsFaulted)
            throw task.Exception!.GetBaseException();
        return task.Result;
    }
}

public sealed class ProgressForm : Form
{
    private readonly Label _lbl = new();

    public ProgressForm(string title)
    {
        Text = title;
        Width = 420;
        Height = 130;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ControlBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("맑은 고딕", 9.5f);

        _lbl.Text = L.T("prog_start");
        _lbl.Dock = DockStyle.Top;
        _lbl.Height = 30;
        _lbl.Padding = new Padding(12, 10, 12, 0);

        var bar = new ProgressBar { Style = ProgressBarStyle.Marquee, Dock = DockStyle.Top, Height = 22 };
        var pad = new Panel { Dock = DockStyle.Top, Height = 8 };

        Controls.Add(bar);
        Controls.Add(pad);
        Controls.Add(_lbl);
    }

    public void ReportText(string text)
    {
        if (IsHandleCreated)
            BeginInvoke(() => _lbl.Text = text);
    }

    public void CloseSafe()
    {
        if (IsHandleCreated) BeginInvoke(Close);
        else Load += (_, _) => Close();
    }
}
