using System.ComponentModel;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MailViewer;

public sealed class MainForm : Form
{
    private MailDb _db;
    private readonly string _dbPath;
    private string? _mboxPath;

    private readonly TextBox _txtKeyword = new();
    private readonly ComboBox _cboField = new();
    private readonly CheckBox _chkThread = new();
    private readonly CheckBox _chkAttached = new();
    private readonly CheckBox _chkDate = new();
    private readonly DateTimePicker _dtFrom = new();
    private readonly DateTimePicker _dtTo = new();
    private readonly Button _btnSearch = new();
    private readonly Button _btnStats = new();
    private readonly Button _btnExport = new();
    private readonly Button _btnReimport = new();
    private readonly Button _btnSync = new();
    private readonly Button _btnSyncSettings = new();
    private bool _syncing;
    private readonly DataGridView _grid = new();
    private readonly Label _lblDetailHeader = new();
    private readonly FlowLayoutPanel _attStrip = new();
    private readonly TabControl _tabs = new();
    private readonly RichTextBox _rtbBody = new();
    private readonly WebView2 _web = new();
    private bool _webReady;
    private readonly ToolStripStatusLabel _status = new();
    private readonly BindingList<MailRow> _rows = new();
    private readonly BindingList<ThreadRow> _threadRows = new();

    private readonly ToolTip _tip = new() { AutoPopDelay = 20000 };
    private MailDetail? _currentDetail;        // 단일 메일 보기 상태
    private LoadedMessage? _currentLoaded;     // mbox에서 즉석 파싱한 결과 (지연 로드)

    public MainForm(string dbPath)
    {
        _dbPath = dbPath;
        _db = new MailDb(dbPath);
        _mboxPath = _db.GetMeta("mbox_path");

        Text = $"메일 뷰어 — {dbPath}";
        Width = 1400;
        Height = 850;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("맑은 고딕", 9.5f);

        BuildLayout();
        RefreshStats();
        RunSearch();
    }

    private void RefreshStats()
    {
        var (min, max, count) = _db.GetStats();
        _status.Text = $"전체 {count:N0}건  ({min} ~ {max})" + (_db.HasFts ? "  [FTS5 전문검색]" : "");
        if (DateTime.TryParse(min, out var dMin)) _dtFrom.Value = dMin.Date;
        if (DateTime.TryParse(max, out var dMax)) _dtTo.Value = dMax.Date;
    }

    private void BuildLayout()
    {
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true, // 내용(한 줄 또는 창이 좁으면 두 줄)에 맞춰 높이 자동
            WrapContents = true,
            Padding = new Padding(8, 8, 8, 4),
        };

        _cboField.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboField.Items.AddRange(new object[] { "전체", "제목", "보낸사람", "받는사람", "본문", "CC" });
        _cboField.SelectedIndex = 0;
        _cboField.Width = 90;

        _txtKeyword.Width = 230;
        _txtKeyword.PlaceholderText = "검색어 (공백 = AND)";
        _txtKeyword.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { RunSearch(); e.SuppressKeyPress = true; } };

        _btnSearch.Text = "검색";
        _btnSearch.Width = 60;
        _btnSearch.Click += (_, _) => RunSearch();

        _chkThread.Text = "스레드로 묶기";
        _chkThread.AutoSize = true;
        _chkThread.CheckedChanged += (_, _) => { SetGridMode(_chkThread.Checked); RunSearch(); };

        _chkAttached.Text = "첨부만";
        _chkAttached.AutoSize = true;
        _chkAttached.CheckedChanged += (_, _) => RunSearch();

        _chkDate.Text = "기간";
        _chkDate.AutoSize = true;
        _chkDate.CheckedChanged += (_, _) =>
        {
            _dtFrom.Enabled = _dtTo.Enabled = _chkDate.Checked;
            RunSearch();
        };

        _dtFrom.Format = DateTimePickerFormat.Short; _dtFrom.Width = 105; _dtFrom.Enabled = false;
        _dtTo.Format = DateTimePickerFormat.Short; _dtTo.Width = 105; _dtTo.Enabled = false;
        var lblTilde = new Label { Text = "~", AutoSize = true, Padding = new Padding(0, 6, 0, 0) };

        _btnStats.Text = "발신자 통계";
        _btnStats.AutoSize = true;
        _btnStats.Click += (_, _) => ShowSenderStats();

        _btnExport.Text = "엑셀 내보내기";
        _btnExport.AutoSize = true;
        _btnExport.Click += (_, _) => ExportExcel();

        _btnReimport.Text = "메일 가져오기";
        _btnReimport.AutoSize = true;
        _btnReimport.Click += (_, _) => Reimport();

        _btnSync.Text = "Gmail 동기화";
        _btnSync.AutoSize = true;
        _btnSync.Click += (_, _) => RunSync();

        _btnSyncSettings.Text = "⚙";
        _btnSyncSettings.Width = 32;
        _btnSyncSettings.Click += (_, _) => ShowSyncSettings();

        foreach (Control c in new Control[] { _cboField, _txtKeyword, _btnSearch, _chkThread, _chkAttached,
                                              _chkDate, _dtFrom, lblTilde, _dtTo, _btnStats, _btnExport,
                                              _btnReimport, _btnSync, _btnSyncSettings })
        {
            c.Margin = new Padding(4, 4, 4, 4);
            top.Controls.Add(c);
        }

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
        };
        // 분할 위치는 실제 크기가 잡힌 뒤에 지정해야 한다 (생성 시점엔 기본 크기 기준으로 클램프됨)
        Shown += (_, _) => split.SplitterDistance = (int)(split.ClientSize.Width * 0.55);

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.AutoGenerateColumns = false;
        _grid.BackgroundColor = SystemColors.Window;
        SetGridMode(threadMode: false);
        _grid.SelectionChanged += (_, _) => ShowSelection();
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || _chkThread.Checked) return;
            if (_grid.CurrentRow?.DataBoundItem is not MailRow row) return;
            var tk = _db.GetThreadKeyOf(row.Id);
            if (tk is not null) ShowThread(tk);
        };

        // ── 상세 패널: 헤더 / 첨부 목록 / [본문|HTML] 탭 ──
        var detailPanel = new Panel { Dock = DockStyle.Fill };

        _lblDetailHeader.Dock = DockStyle.Top;
        _lblDetailHeader.AutoSize = false;
        _lblDetailHeader.Height = 112;
        _lblDetailHeader.Padding = new Padding(8);
        _lblDetailHeader.BackColor = SystemColors.ControlLight;
        _lblDetailHeader.AutoEllipsis = true;

        _attStrip.Dock = DockStyle.Top;
        _attStrip.Height = 34;
        _attStrip.Padding = new Padding(6, 3, 6, 0);
        _attStrip.BackColor = Color.FromArgb(255, 250, 230);
        _attStrip.Visible = false;

        _rtbBody.Dock = DockStyle.Fill;
        _rtbBody.ReadOnly = true;
        _rtbBody.BorderStyle = BorderStyle.None;
        _rtbBody.Font = new Font("맑은 고딕", 10f);

        _web.Dock = DockStyle.Fill;

        _tabs.Dock = DockStyle.Fill;
        var tabText = new TabPage("본문");
        tabText.Controls.Add(_rtbBody);
        var tabHtml = new TabPage("HTML 원본");
        tabHtml.Controls.Add(_web);
        _tabs.TabPages.Add(tabText);
        _tabs.TabPages.Add(tabHtml);
        _tabs.SelectedIndexChanged += (_, _) => { if (_tabs.SelectedIndex == 1) RenderHtmlTab(); };

        detailPanel.Controls.Add(_tabs);
        detailPanel.Controls.Add(_attStrip);
        detailPanel.Controls.Add(_lblDetailHeader);

        split.Panel1.Controls.Add(_grid);
        split.Panel2.Controls.Add(detailPanel);

        var statusBar = new StatusStrip();
        statusBar.Items.Add(_status);

        Controls.Add(split);
        Controls.Add(top);
        Controls.Add(statusBar);
        split.BringToFront();
    }

    private void SetGridMode(bool threadMode)
    {
        _grid.DataSource = null;
        _grid.Columns.Clear();
        if (threadMode)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "최근", DataPropertyName = nameof(ThreadRow.LastDate), Width = 115 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "스레드 제목", DataPropertyName = nameof(ThreadRow.Subject), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "메일수", DataPropertyName = nameof(ThreadRow.Count), Width = 60 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "참여자", DataPropertyName = nameof(ThreadRow.Senders), Width = 60 });
            _grid.DataSource = _threadRows;
        }
        else
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "날짜", DataPropertyName = nameof(MailRow.Date), Width = 115 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "보낸사람", DataPropertyName = nameof(MailRow.FromName), Width = 110 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "제목", DataPropertyName = nameof(MailRow.Subject), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "📎", DataPropertyName = nameof(MailRow.AttCount), Width = 45 });
            _grid.DataSource = _rows;
        }
    }

    private (string keyword, string field, DateTime? from, DateTime? to) CurrentFilter() =>
        (_txtKeyword.Text, (string)_cboField.SelectedItem!,
         _chkDate.Checked ? _dtFrom.Value.Date : null,
         _chkDate.Checked ? _dtTo.Value.Date : null);

    private void RunSearch()
    {
        var (kw, field, from, to) = CurrentFilter();

        if (_chkThread.Checked)
        {
            var (threads, total) = _db.SearchThreads(kw, field, from, to, _chkAttached.Checked);
            Refill(_threadRows, threads);
            _status.Text = $"스레드 {total:N0}개";
            if (threads.Count == 0) ClearDetail();
            return;
        }

        var (rows, mailTotal) = _db.Search(kw, field, from, to, _chkAttached.Checked);
        Refill(_rows, rows);
        _status.Text = $"검색 결과 {mailTotal:N0}건";
        if (rows.Count == 0) ClearDetail();
    }

    private static void Refill<T>(BindingList<T> list, List<T> items)
    {
        list.RaiseListChangedEvents = false;
        list.Clear();
        foreach (var i in items) list.Add(i);
        list.RaiseListChangedEvents = true;
        list.ResetBindings();
    }

    private void ClearDetail()
    {
        _currentDetail = null;
        _currentLoaded = null;
        _lblDetailHeader.Text = "";
        _rtbBody.Text = "";
        _attStrip.Visible = false;
    }

    private void ShowSelection()
    {
        if (_grid.CurrentRow?.DataBoundItem is ThreadRow thread)
        {
            ShowThread(thread.Key);
            return;
        }
        if (_grid.CurrentRow?.DataBoundItem is not MailRow row) return;
        var d = _db.GetDetail(row.Id);
        if (d is null) return;

        _currentDetail = d;
        _currentLoaded = null;

        _lblDetailHeader.Text =
            $"제목: {d.Subject}\n날짜: {d.Date}    보낸사람: {d.FromFull}\n" +
            $"받는사람: {(d.ToFull.Length > 0 ? d.ToFull : "(없음)")}\n" +
            $"CC: {(d.Cc.Length > 0 ? d.Cc : "(없음)")}" +
            (d.Labels.Length > 0 ? $"    라벨: {d.Labels}" : "");
        _tip.SetToolTip(_lblDetailHeader, _lblDetailHeader.Text); // 잘린 내용은 마우스 오버로 전체 확인

        FillAttachmentStrip(d);

        _rtbBody.Text = d.Body;
        HighlightKeywords();

        if (_tabs.SelectedIndex == 1) RenderHtmlTab();
    }

    private void FillAttachmentStrip(MailDetail d)
    {
        _attStrip.Controls.Clear();
        if (d.AttCount == 0) { _attStrip.Visible = false; return; }

        var names = d.AttNames.Split("; ", StringSplitOptions.None);
        for (int i = 0; i < names.Length; i++)
        {
            var btn = new Button
            {
                Text = $"📎 {names[i]}",
                AutoSize = true,
                Tag = i,
                Margin = new Padding(2, 0, 2, 0),
            };
            btn.Click += (_, _) => SaveAttachment(d, (int)btn.Tag!, names[(int)btn.Tag!]);
            _attStrip.Controls.Add(btn);
        }
        _attStrip.Visible = true;
    }

    private bool EnsureLoaded(MailDetail d)
    {
        if (_currentLoaded is not null) return true;
        if (d.MboxOffset is null || d.MboxLength is null)
        {
            MessageBox.Show(this, "이 메일은 CSV에서 가져와 원본(mbox)이 없습니다.", "안내",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        string? path = d.MboxFile == 1 ? _db.GetMeta("sync_mbox_path") : _mboxPath;
        if (path is null || !File.Exists(path))
        {
            MessageBox.Show(this, $"mbox 파일을 찾을 수 없습니다:\n{path}\n\n" +
                "파일을 옮겼다면 [메일 가져오기]로 다시 가져와 주세요.", "오류",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        try
        {
            UseWaitCursor = true;
            _currentLoaded = MessageLoader.Load(path, d.MboxOffset.Value, d.MboxLength.Value);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "메시지 파싱 실패:\n" + ex.Message, "오류",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void SaveAttachment(MailDetail d, int index, string name)
    {
        if (!EnsureLoaded(d)) return;
        if (index >= _currentLoaded!.Attachments.Count)
        {
            MessageBox.Show(this, "첨부 목록이 일치하지 않습니다.", "오류");
            return;
        }
        var att = _currentLoaded.Attachments[index];

        using var dlg = new SaveFileDialog { FileName = name, Title = "첨부파일 저장" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            MessageLoader.SaveAttachment(att, dlg.FileName);
            _status.Text = $"저장됨: {dlg.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "저장 실패:\n" + ex.Message, "오류",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ── HTML 렌더링 (WebView2, 지연 초기화) ──

    private async void RenderHtmlTab()
    {
        string html;
        if (_currentDetail is null)
            html = "<p style='color:gray'>메일을 선택하세요.</p>";
        else if (!_currentDetail.HasHtml)
            html = "<p style='color:gray'>이 메일에는 HTML 본문이 없습니다. [본문] 탭을 확인하세요.</p>";
        else if (!EnsureLoaded(_currentDetail) || _currentLoaded!.Html is null)
            html = "<p style='color:gray'>HTML 본문을 불러올 수 없습니다.</p>";
        else
            html = _currentLoaded.Html;

        try
        {
            if (!_webReady)
            {
                string dataDir = Path.Combine(Path.GetTempPath(), "MailViewer.WebView2");
                var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
                await _web.EnsureCoreWebView2Async(env);
                _web.CoreWebView2.Settings.IsScriptEnabled = false;
                _web.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
                _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _webReady = true;
            }

            // NavigateToString은 2MB 제한이 있어 임시 파일 경유 (인라인 이미지가 크면 초과)
            string tmpDir = Path.Combine(Path.GetTempPath(), "MailViewer");
            Directory.CreateDirectory(tmpDir);
            string tmpFile = Path.Combine(tmpDir, "view.html");
            File.WriteAllText(tmpFile, MessageLoader.BuildSafeHtml(html), System.Text.Encoding.UTF8);
            _web.CoreWebView2.Navigate(new Uri(tmpFile).AbsoluteUri);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "HTML 뷰어 초기화 실패 (WebView2 런타임 필요):\n" + ex.Message, "오류",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ── 스레드 대화 뷰 ──

    private void ShowThread(string threadKey)
    {
        var mails = _db.GetThreadMails(threadKey);
        if (mails.Count == 0) return;

        _currentDetail = null;
        _currentLoaded = null;
        _attStrip.Visible = false;
        _tabs.SelectedIndex = 0;

        var senders = mails.Select(m => CsvImporter.ParseSender(m.FromFull).name)
                           .Where(n => n.Length > 0).Distinct().ToList();
        string senderList = string.Join(", ", senders.Take(8)) + (senders.Count > 8 ? $" 외 {senders.Count - 8}명" : "");
        string title = CsvImporter.NormalizeSubject(mails[^1].Subject);
        if (title.Length == 0) title = "(제목 없음)";
        _lblDetailHeader.Text =
            $"스레드: {title}\n메일 {mails.Count:N0}건    기간: {mails[0].Date} ~ {mails[^1].Date}\n" +
            $"참여자: {senderList}";
        _tip.SetToolTip(_lblDetailHeader, _lblDetailHeader.Text);

        var sb = new System.Text.StringBuilder();
        var separators = new List<(int start, int length)>();
        for (int i = 0; i < mails.Count; i++)
        {
            var m = mails[i];
            string header = $"[{i + 1}/{mails.Count}] {m.Date}  {m.FromFull}\n제목: {m.Subject}" +
                            (m.AttCount > 0 ? $"\n첨부: {m.AttNames}" : "") +
                            (m.Cc.Length > 0 ? $"\nCC: {m.Cc}" : "");
            separators.Add((sb.Length, header.Length));
            sb.Append(header).Append("\n\n").Append(m.Body).Append("\n\n\n");
        }

        _rtbBody.Text = sb.ToString();
        foreach (var (start, length) in separators)
        {
            _rtbBody.Select(start, length);
            _rtbBody.SelectionFont = new Font(_rtbBody.Font, FontStyle.Bold);
            _rtbBody.SelectionBackColor = Color.FromArgb(226, 234, 248);
        }
        HighlightKeywords();
        _rtbBody.Select(0, 0);
    }

    private void HighlightKeywords()
    {
        var terms = _txtKeyword.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return;

        string text = _rtbBody.Text;
        foreach (var term in terms)
        {
            int idx = 0, safety = 0;
            while ((idx = text.IndexOf(term, idx, StringComparison.OrdinalIgnoreCase)) >= 0 && safety++ < 500)
            {
                _rtbBody.Select(idx, term.Length);
                _rtbBody.SelectionBackColor = Color.Yellow;
                idx += term.Length;
            }
        }
        _rtbBody.Select(0, 0);
    }

    // ── 통계 / 내보내기 / 가져오기 ──

    private string FilterSummary()
    {
        var (kw, field, from, to) = CurrentFilter();
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(kw)) parts.Add($"{field} \"{kw.Trim()}\"");
        if (from is not null) parts.Add($"{from:yyyy-MM-dd} ~ {to:yyyy-MM-dd}");
        if (_chkAttached.Checked) parts.Add("첨부만");
        return parts.Count > 0 ? string.Join(", ", parts) : "전체 메일";
    }

    private void ShowSenderStats()
    {
        var (kw, field, from, to) = CurrentFilter();
        var stats = _db.GetSenderStats(kw, field, from, to);
        using var dlg = new SenderStatsForm(stats, FilterSummary());
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.SelectedSender is not null)
        {
            _cboField.SelectedItem = "보낸사람";
            _txtKeyword.Text = dlg.SelectedSender;
            RunSearch();
        }
    }

    private void ExportExcel()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "엑셀로 내보내기",
            Filter = "Excel 파일 (*.xlsx)|*.xlsx",
            FileName = $"메일검색결과_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            UseWaitCursor = true;
            var (kw, field, from, to) = CurrentFilter();
            var mails = _db.GetAllDetails(kw, field, from, to, _chkAttached.Checked);
            ExcelExporter.Export(mails, dlg.FileName);
            UseWaitCursor = false;
            var answer = MessageBox.Show(this,
                $"{mails.Count:N0}건을 저장했습니다.\n{dlg.FileName}\n\n지금 열어볼까요?",
                "내보내기 완료", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer == DialogResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "내보내기 실패:\n" + ex.Message, "오류",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void ShowSyncSettings()
    {
        using var dlg = new GmailSettingsForm(_dbPath);
        dlg.ShowDialog(this);
    }

    private async void RunSync()
    {
        if (_syncing) return;
        if (GmailSync.LoadAccount(_dbPath) is null)
        {
            using var dlg = new GmailSettingsForm(_dbPath);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
        }

        _syncing = true;
        _btnSync.Enabled = false;
        string prevStatus = _status.Text;
        try
        {
            var (added, skipped) = await Task.Run(() =>
                GmailSync.Sync(_dbPath, msg => BeginInvoke(() => _status.Text = $"[동기화] {msg}")));

            // 검색 연결을 새로 열어 새 메일 반영
            _db.Dispose();
            _db = new MailDb(_dbPath);
            RefreshStats();
            RunSearch();
            _status.Text = added > 0
                ? $"동기화 완료: 새 메일 {added:N0}건 추가" + (skipped > 0 ? $" (중복 {skipped:N0}건 건너뜀)" : "")
                : "동기화 완료: 새 메일 없음";
        }
        catch (Exception ex)
        {
            _status.Text = prevStatus;
            MessageBox.Show(this, "동기화 실패:\n" + ex.Message +
                "\n\n앱 비밀번호가 맞는지 ⚙ 설정에서 [연결 테스트]로 확인해 보세요.", "오류",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _syncing = false;
            _btnSync.Enabled = true;
        }
    }

    private void Reimport()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "가져올 메일 파일 선택 (Gmail 백업 mbox 또는 CSV)",
            Filter = "메일 파일 (*.mbox;*.csv)|*.mbox;*.csv|모든 파일 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _db.Dispose();
            int n = ImportUi.Run(this, dlg.FileName, _dbPath);
            MessageBox.Show(this, $"메일 {n:N0}건을 가져왔습니다.", "가져오기 완료",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "가져오기 실패:\n" + ex.Message, "오류",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _db = new MailDb(_dbPath);
            _mboxPath = _db.GetMeta("mbox_path");
        }

        ClearDetail();
        RefreshStats();
        RunSearch();
    }
}
