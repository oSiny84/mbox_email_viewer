using System.Data;

namespace MailViewer;

/// <summary>발신자별 통계 창. 행을 더블클릭하면 해당 발신자로 검색하도록 선택값을 돌려준다.</summary>
public sealed class SenderStatsForm : Form
{
    public string? SelectedSender { get; private set; }

    public SenderStatsForm(List<SenderStat> stats, string conditionSummary)
    {
        Text = $"발신자별 통계 — {stats.Count:N0}명";
        Width = 860;
        Height = 640;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("맑은 고딕", 9.5f);

        var lbl = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(8, 6, 8, 0),
            Text = $"조건: {conditionSummary}    (열 머리글 클릭 = 정렬, 행 더블클릭 = 해당 발신자로 검색)",
        };

        // DataTable 바인딩이면 헤더 클릭 정렬이 기본 제공됨
        var table = new DataTable();
        table.Columns.Add("발신자", typeof(string));
        table.Columns.Add("이메일", typeof(string));
        table.Columns.Add("건수", typeof(int));
        table.Columns.Add("최초", typeof(string));
        table.Columns.Add("최근", typeof(string));
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
            grid.Columns["발신자"]!.Width = 140;
            grid.Columns["이메일"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            grid.Columns["건수"]!.Width = 70;
            grid.Columns["최초"]!.Width = 95;
            grid.Columns["최근"]!.Width = 95;
        };
        grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            var r = ((DataRowView)grid.Rows[e.RowIndex].DataBoundItem!).Row;
            string email = (string)r["이메일"];
            SelectedSender = email.Length > 0 ? email : (string)r["발신자"];
            DialogResult = DialogResult.OK;
        };

        Controls.Add(grid);
        Controls.Add(lbl);
        grid.BringToFront();
    }
}
