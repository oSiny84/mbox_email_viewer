using System.Data;

namespace MailViewer;

/// <summary>발신자별 통계 창. 행을 더블클릭하면 해당 발신자로 검색하도록 선택값을 돌려준다.</summary>
public sealed class SenderStatsForm : Form
{
    public string? SelectedSender { get; private set; }

    public SenderStatsForm(List<SenderStat> stats, string conditionSummary)
    {
        Text = L.F("stats_title", stats.Count);
        Width = 860;
        Height = 640;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("맑은 고딕", 9.5f);

        var lbl = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(8, 6, 8, 0),
            Text = L.F("stats_cond", conditionSummary),
        };

        // DataTable 바인딩이면 헤더 클릭 정렬이 기본 제공됨
        var table = new DataTable();
        table.Columns.Add(L.T("stats_col_sender"), typeof(string));
        table.Columns.Add(L.T("stats_col_email"), typeof(string));
        table.Columns.Add(L.T("stats_col_count"), typeof(int));
        table.Columns.Add(L.T("stats_col_first"), typeof(string));
        table.Columns.Add(L.T("stats_col_last"), typeof(string));
        foreach (var s in stats)
            table.Rows.Add(s.Name, s.Email, s.Count, s.FirstDate, s.LastDate);

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BackgroundColor = SystemColors.Window,
            DataSource = table,
        };
        grid.DataBindingComplete += (_, _) =>
        {
            grid.Columns[0]!.Width = 140;
            grid.Columns[1]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            grid.Columns[2]!.Width = 70;
            grid.Columns[3]!.Width = 115;
            grid.Columns[4]!.Width = 115;
        };
        grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            var r = ((DataRowView)grid.Rows[e.RowIndex].DataBoundItem!).Row;
            string email = (string)r[1];
            SelectedSender = email.Length > 0 ? email : (string)r[0];
            DialogResult = DialogResult.OK;
        };

        Controls.Add(grid);
        Controls.Add(lbl);
        grid.BringToFront();
    }
}
