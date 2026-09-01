namespace MailViewer;

/// <summary>mbox/CSV 가져오기를 진행 표시 창과 함께 실행하는 공용 로직.</summary>
public static class ImportUi
{
    public static int RunHeadless(string sourcePath, string dbPath, Action<int>? progress = null) =>
        Path.GetExtension(sourcePath).Equals(".csv", StringComparison.OrdinalIgnoreCase)
            ? CsvImporter.Import(sourcePath, dbPath, progress)
            : MboxImporter.Import(sourcePath, dbPath, progress);

    public static int Run(IWin32Window? owner, string sourcePath, string dbPath)
    {
        using var pf = new ProgressForm($"가져오는 중: {Path.GetFileName(sourcePath)}");
        var task = Task.Run(() => RunHeadless(sourcePath, dbPath, pf.Report));
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

        _lbl.Text = "가져오는 중...";
        _lbl.Dock = DockStyle.Top;
        _lbl.Height = 30;
        _lbl.Padding = new Padding(12, 10, 12, 0);

        var bar = new ProgressBar { Style = ProgressBarStyle.Marquee, Dock = DockStyle.Top, Height = 22 };
        var pad = new Panel { Dock = DockStyle.Top, Height = 8 };

        Controls.Add(bar);
        Controls.Add(pad);
        Controls.Add(_lbl);
    }

    public void Report(int n)
    {
        if (IsHandleCreated)
            BeginInvoke(() => _lbl.Text = $"가져오는 중... {n:N0}건");
    }

    public void CloseSafe()
    {
        if (IsHandleCreated) BeginInvoke(Close);
        else Load += (_, _) => Close();
    }
}
