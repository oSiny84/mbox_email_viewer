using System.ComponentModel;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MailViewer;

public sealed class MainForm : Form
{
    // 검색 필드 콤보 인덱스 → 언어 중립 키 (MailDb.BuildWhere와 약속)
    private static readonly string[] FieldKeys = { "all", "subject", "from", "to", "body", "cc" };

    private MailDb _db;
    private readonly string _dbPath;

    private readonly TextBox _txtKeyword = new();
    private readonly ComboBox _cboField = new();
    private readonly CheckBox _chkThread = new();
    private readonly CheckBox _chkAttached = new();
    private readonly CheckBox _chkTrash = new();
    private readonly CheckBox _chkDate = new();
    private readonly DateTimePicker _dtFrom = new();
    private readonly DateTimePicker _dtTo = new();
    private readonly Button _btnSearch = new();
    private readonly Button _btnStats = new();
    private readonly Button _btnExport = new();
    private readonly Button _btnReimport = new();
    private readonly Button _btnSync = new();
    private readonly Button _btnSplit = new();
    private readonly Button _btnBackup = new();
    private readonly Button _btnSettings = new();
    private bool _syncing;
    private readonly DataGridView _grid = new();
    private readonly Label _lblDetailHeader = new();
    private readonly FlowLayoutPanel _attStrip = new();
    private readonly TabControl _tabs = new();
    private readonly TabPage _tabBody = new();
    private readonly TabPage _tabHtml = new();
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

        Width = 1400;
        Height = 850;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("맑은 고딕", 9.5f);

        BuildLayout();
        ApplyTexts();
        RefreshStats();
        RunSearch();
    }

    /// <summary>현재 언어로 모든 UI 텍스트 적용. 언어 변경 시 재호출.</summary>
    private void ApplyTexts()
    {
        Text = $"{L.T("title")} — {_dbPath}";

        int fieldIdx = Math.Max(_cboField.SelectedIndex, 0);
        _cboField.Items.Clear();
        _cboField.Items.AddRange(new object[]
        {
            L.T("fld_all"), L.T("fld_subject"), L.T("fld_from"),
            L.T("fld_to"), L.T("fld_body"), L.T("fld_cc"),
        });
        _cboField.SelectedIndex = fieldIdx;

        _txtKeyword.PlaceholderText = L.T("kw_placeholder");
        _btnSearch.Text = L.T("btn_search");
        _chkThread.Text = L.T("chk_thread");
        _chkAttached.Text = L.T("chk_attached");
        _chkTrash.Text = L.T("chk_trash");
        _chkDate.Text = L.T("chk_date");
        _btnStats.Text = L.T("btn_stats");
        _btnExport.Text = L.T("btn_export");
        _btnReimport.Text = L.T("btn_import");
        _btnSync.Text = L.T("btn_sync");
        _btnSplit.Text = L.T("btn_split");
        _tip.SetToolTip(_btnSplit, L.T("btn_split_tip"));
        _btnBackup.Text = L.T("btn_backup");
        _tip.SetToolTip(_btnBackup, L.T("btn_backup_tip"));
        _tip.SetToolTip(_btnSettings, L.T("set_title"));
        _tabBody.Text = L.T("tab_body");
        _tabHtml.Text = L.T("tab_html");

        SetGridMode(_chkThread.Checked); // 열 머리글 다시 적용
    }

    private void RefreshStats()
    {
        var (min, max, count) = _db.GetStats();
        _status.Text = L.F("status_total", count, min, max) + (_db.HasFts ? L.T("status_fts") : "");
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
        _cboField.Width = 100;

        _txtKeyword.Width = 230;
        _txtKeyword.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { RunSearch(); e.SuppressKeyPress = true; } };

        _btnSearch.Width = 70;
        _btnSearch.Click += (_, _) => RunSearch();

        _chkThread.AutoSize = true;
        _chkThread.CheckedChanged += (_, _) => { SetGridMode(_chkThread.Checked); RunSearch(); };

        _chkAttached.AutoSize = true;
        _chkAttached.CheckedChanged += (_, _) => RunSearch();

        _chkTrash.AutoSize = true;
        _chkTrash.CheckedChanged += (_, _) =>
        {
            // 휴지통 모드에서는 스레드 묶기 없이 평면 목록만
            if (_chkTrash.Checked) _chkThread.Checked = false;
            _chkThread.Enabled = !_chkTrash.Checked;
            RunSearch();
        };

        _chkDate.AutoSize = true;
        _chkDate.CheckedChanged += (_, _) =>
        {
            _dtFrom.Enabled = _dtTo.Enabled = _chkDate.Checked;
            RunSearch();
        };

        _dtFrom.Format = DateTimePickerFormat.Short; _dtFrom.Width = 105; _dtFrom.Enabled = false;
        _dtTo.Format = DateTimePickerFormat.Short; _dtTo.Width = 105; _dtTo.Enabled = false;
        var lblTilde = new Label { Text = "~", AutoSize = true, Padding = new Padding(0, 6, 0, 0) };

        _btnStats.AutoSize = true;
        _btnStats.Click += (_, _) => ShowSenderStats();

        _btnExport.AutoSize = true;
        _btnExport.Click += (_, _) => ExportExcel();

        _btnReimport.AutoSize = true;
        _btnReimport.Click += (_, _) => Reimport();

        _btnSync.AutoSize = true;
        _btnSync.Click += (_, _) => RunSync();

        _btnSplit.AutoSize = true;
        _btnSplit.Click += (_, _) => RunSplit();

        _btnBackup.AutoSize = true;
        _btnBackup.Click += (_, _) => RunBackup();

        _btnSettings.Text = "⚙";
        _btnSettings.Width = 32;
        _btnSettings.Click += (_, _) => ShowSettings();

        foreach (Control c in new Control[] { _cboField, _txtKeyword, _btnSearch, _chkThread, _chkAttached,
                                              _chkTrash, _chkDate, _dtFrom, lblTilde, _dtTo, _btnStats, _btnExport,
                                              _btnReimport, _btnSync, _btnSplit, _btnBackup, _btnSettings })
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
        _grid.MultiSelect = true; // Ctrl/Shift로 여러 개 선택해서 한 번에 삭제 가능
        _grid.AutoGenerateColumns = false;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.SelectionChanged += (_, _) => ShowSelection();
        _grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete && !_chkThread.Checked)
            {
                DeleteOrRestoreSelected();
                e.Handled = true;
            }
        };
        var gridMenu = new ContextMenuStrip();
        var miStar = gridMenu.Items.Add("");
        miStar.Click += (_, _) => ToggleStarSelected();
        var miDelete = gridMenu.Items.Add("");
        miDelete.Click += (_, _) => DeleteOrRestoreSelected();
        gridMenu.Opening += (_, e) =>
        {
            if (_chkThread.Checked || _grid.SelectedRows.Count == 0) { e.Cancel = true; return; }
            bool anyUnstarred = _grid.SelectedRows.Cast<DataGridViewRow>()
                .Select(r => r.DataBoundItem).OfType<MailRow>().Any(m => m.Star.Length == 0);
            miStar.Text = anyUnstarred ? L.T("menu_star") : L.T("menu_unstar");
            miStar.Visible = !_chkTrash.Checked;
            miDelete.Text = _chkTrash.Checked ? L.T("menu_restore") : L.T("menu_delete");
        };
        _grid.ContextMenuStrip = gridMenu;
        _grid.CellClick += (_, e) =>
        {
            // ★ 열 클릭으로 즐겨찾기 토글 (일반 목록에서만)
            if (e.RowIndex < 0 || _chkThread.Checked || _chkTrash.Checked) return;
            if (_grid.Columns[e.ColumnIndex].DataPropertyName != nameof(MailRow.Star)) return;
            ToggleStarSelected();
        };
        _grid.CellDoubleClick += (_, e) =>
        {
            // 일반 모드에서 메일을 더블클릭하면 그 메일이 속한 스레드 대화를 오른쪽에 표시
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
        _tabBody.Controls.Add(_rtbBody);
        _tabHtml.Controls.Add(_web);
        _tabs.TabPages.Add(_tabBody);
        _tabs.TabPages.Add(_tabHtml);
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
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = L.T("col_no"),
            DataPropertyName = threadMode ? nameof(ThreadRow.No) : nameof(MailRow.No),
            Width = 55,
        });
        if (threadMode)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L.T("col_last"), DataPropertyName = nameof(ThreadRow.LastDate), Width = 115 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L.T("col_thread_subject"), DataPropertyName = nameof(ThreadRow.Subject), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L.T("col_count"), DataPropertyName = nameof(ThreadRow.Count), Width = 60 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L.T("col_senders"), DataPropertyName = nameof(ThreadRow.Senders), Width = 60 });
            _grid.DataSource = _threadRows;
        }
        else
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "★", DataPropertyName = nameof(MailRow.Star), Width = 34,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter, ForeColor = Color.Goldenrod },
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L.T("col_date"), DataPropertyName = nameof(MailRow.Date), Width = 115 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L.T("col_from"), DataPropertyName = nameof(MailRow.FromName), Width = 110 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L.T("col_subject"), DataPropertyName = nameof(MailRow.Subject), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L.T("col_att"), DataPropertyName = nameof(MailRow.AttCount), Width = 45 });
            _grid.DataSource = _rows;
        }
    }

    private (string keyword, string field, DateTime? from, DateTime? to) CurrentFilter() =>
        (_txtKeyword.Text, FieldKeys[Math.Max(_cboField.SelectedIndex, 0)],
         _chkDate.Checked ? _dtFrom.Value.Date : null,
         _chkDate.Checked ? _dtTo.Value.Date : null);

    private void RunSearch()
    {
        var (kw, field, from, to) = CurrentFilter();

        if (_chkTrash.Checked)
        {
            var (trashRows, trashTotal) = _db.Search(kw, field, from, to, _chkAttached.Checked, trash: true);
            Refill(_rows, trashRows);
            _status.Text = L.F("status_trash", trashTotal);
            if (trashRows.Count == 0) ClearDetail();
            return;
        }

        if (_chkThread.Checked)
        {
            var (threads, total) = _db.SearchThreads(kw, field, from, to, _chkAttached.Checked);
            Refill(_threadRows, threads);
            _status.Text = L.F("status_threads", total);
            if (threads.Count == 0) ClearDetail();
            return;
        }

        var (rows, mailTotal) = _db.Search(kw, field, from, to, _chkAttached.Checked);
        Refill(_rows, rows);
        _status.Text = L.F("status_results", mailTotal);
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
            $"{L.T("hdr_subject")}: {d.Subject}\n" +
            $"{L.T("hdr_date")}: {d.Date}    {L.T("hdr_from")}: {d.FromFull}\n" +
            $"{L.T("hdr_to")}: {(d.ToFull.Length > 0 ? d.ToFull : L.T("none"))}\n" +
            $"{L.T("hdr_cc")}: {(d.Cc.Length > 0 ? d.Cc : L.T("none"))}" +
            (d.Labels.Length > 0 ? $"    {L.T("hdr_labels")}: {d.Labels}" : "");
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
            MessageBox.Show(this, L.T("msg_csv_no_mbox"), L.T("info"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        string? path = _db.GetMboxPath(d.MboxFile);
        if (path is null || !File.Exists(path))
        {
            MessageBox.Show(this, L.F("msg_mbox_missing", path ?? ""), L.T("err"),
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
            MessageBox.Show(this, L.T("err_parse") + ex.Message, L.T("err"),
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
            MessageBox.Show(this, L.T("att_mismatch"), L.T("err"));
            return;
        }
        var att = _currentLoaded.Attachments[index];

        using var dlg = new SaveFileDialog { FileName = name, Title = L.T("save_att_title") };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            MessageLoader.SaveAttachment(att, dlg.FileName);
            _status.Text = L.F("status_saved", dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.T("save_att_fail") + ex.Message, L.T("err"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ── HTML 렌더링 (WebView2, 지연 초기화) ──

    private async void RenderHtmlTab()
    {
        string html;
        if (_currentDetail is null)
            html = $"<p style='color:gray'>{L.T("html_select")}</p>";
        else if (!_currentDetail.HasHtml)
            html = $"<p style='color:gray'>{L.T("html_none")}</p>";
        else if (!EnsureLoaded(_currentDetail) || _currentLoaded!.Html is null)
            html = $"<p style='color:gray'>{L.T("html_load_fail")}</p>";
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
            MessageBox.Show(this, L.T("err_webview") + ex.Message, L.T("err"),
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
        string senderList = string.Join(", ", senders.Take(8)) + (senders.Count > 8 ? L.F("more_n", senders.Count - 8) : "");
        string title = CsvImporter.NormalizeSubject(mails[^1].Subject);
        if (title.Length == 0) title = L.T("no_subject");
        _lblDetailHeader.Text =
            $"{L.T("hdr_thread")}: {title}\n" +
            $"{L.F("hdr_mails", mails.Count)}    {L.T("hdr_period")}: {mails[0].Date} ~ {mails[^1].Date}\n" +
            $"{L.T("hdr_participants")}: {senderList}";
        _tip.SetToolTip(_lblDetailHeader, _lblDetailHeader.Text);

        var sb = new System.Text.StringBuilder();
        var separators = new List<(int start, int length)>();
        for (int i = 0; i < mails.Count; i++)
        {
            var m = mails[i];
            string header = $"[{i + 1}/{mails.Count}] {m.Date}  {m.FromFull}\n{L.T("hdr_subject")}: {m.Subject}" +
                            (m.AttCount > 0 ? $"\n{L.T("hdr_att")}: {m.AttNames}" : "") +
                            (m.Cc.Length > 0 ? $"\n{L.T("hdr_cc")}: {m.Cc}" : "");
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

    // ── 즐겨찾기(별표) ──

    private void ToggleStarSelected()
    {
        var mails = _grid.SelectedRows.Cast<DataGridViewRow>()
            .Select(r => r.DataBoundItem).OfType<MailRow>().ToList();
        if (mails.Count == 0) return;

        // 하나라도 별표가 없으면 전체 별표, 모두 별표면 전체 해제
        bool star = mails.Any(m => m.Star.Length == 0);
        long currentId = (_grid.CurrentRow?.DataBoundItem as MailRow)?.Id ?? -1;

        try
        {
            MailDb.SetStarred(_dbPath, mails.Select(m => m.Id).ToList(), star);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, L.T("err"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        RunSearch(); // 별표 우선 정렬로 다시 그림

        // 방금 조작한 메일 다시 선택 (맨 위로 이동했을 수 있음)
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.DataBoundItem is MailRow m && m.Id == currentId)
            {
                _grid.CurrentCell = row.Cells[0];
                break;
            }
        }
    }

    // ── 삭제(휴지통) / 복원 ──

    private void DeleteOrRestoreSelected()
    {
        var ids = _grid.SelectedRows.Cast<DataGridViewRow>()
            .Select(r => r.DataBoundItem).OfType<MailRow>()
            .Select(m => m.Id).ToList();
        if (ids.Count == 0) return;

        bool restoring = _chkTrash.Checked;
        if (!restoring)
        {
            var answer = MessageBox.Show(this, L.F("confirm_delete", ids.Count), L.T("confirm"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
        }

        try
        {
            MailDb.SetDeleted(_dbPath, ids, deleted: !restoring);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, L.T("err"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        ClearDetail();
        RefreshStats();
        RunSearch();
        _status.Text = restoring ? L.F("status_restored", ids.Count) : L.F("status_deleted", ids.Count);
    }

    // ── 통계 / 내보내기 / 가져오기 / 동기화 / 설정 ──

    private string FilterSummary()
    {
        var (kw, _, from, to) = CurrentFilter();
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(kw)) parts.Add($"{_cboField.SelectedItem} \"{kw.Trim()}\"");
        if (from is not null) parts.Add($"{from:yyyy-MM-dd} ~ {to:yyyy-MM-dd}");
        if (_chkAttached.Checked) parts.Add(L.T("filter_attached"));
        return parts.Count > 0 ? string.Join(", ", parts) : L.T("filter_all_mail");
    }

    private void ShowSenderStats()
    {
        var (kw, field, from, to) = CurrentFilter();
        var stats = _db.GetSenderStats(kw, field, from, to);
        using var dlg = new SenderStatsForm(stats, FilterSummary());
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.SelectedSender is not null)
        {
            _cboField.SelectedIndex = Array.IndexOf(FieldKeys, "from");
            _txtKeyword.Text = dlg.SelectedSender;
            RunSearch();
        }
    }

    private void ExportExcel()
    {
        using var dlg = new SaveFileDialog
        {
            Title = L.T("export_title"),
            Filter = "Excel (*.xlsx)|*.xlsx",
            FileName = L.F("export_file", DateTime.Now.ToString("yyyyMMdd_HHmmss")),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            UseWaitCursor = true;
            var (kw, field, from, to) = CurrentFilter();
            var mails = _db.GetAllDetails(kw, field, from, to, _chkAttached.Checked);
            ExcelExporter.Export(mails, dlg.FileName);
            UseWaitCursor = false;
            var answer = MessageBox.Show(this, L.F("export_done", mails.Count, dlg.FileName),
                L.T("export_done_title"), MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer == DialogResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.T("export_fail") + ex.Message, L.T("err"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void ShowSettings()
    {
        string prevLang = L.Lang;
        using var dlg = new SettingsForm(_dbPath);
        dlg.ShowDialog(this);
        if (L.Lang != prevLang)
        {
            ApplyTexts();
            RefreshStats();
            RunSearch();
        }
    }

    private async void RunSync()
    {
        if (_syncing) return;
        if (GmailSync.LoadAccount(_dbPath) is null)
        {
            using var dlg = new SettingsForm(_dbPath);
            if (dlg.ShowDialog(this) != DialogResult.OK || GmailSync.LoadAccount(_dbPath) is null) return;
        }

        _syncing = true;
        _btnSync.Enabled = false;
        string prevStatus = _status.Text;
        try
        {
            var (added, skipped) = await Task.Run(() =>
                GmailSync.Sync(_dbPath, msg => BeginInvoke(() => _status.Text = L.T("sync_prefix") + msg)));

            // 검색 연결을 새로 열어 새 메일 반영
            _db.Dispose();
            _db = new MailDb(_dbPath);
            RefreshStats();
            RunSearch();
            _status.Text = added > 0
                ? L.F("sync_done_new", added) + (skipped > 0 ? L.F("sync_done_dup", skipped) : "")
                : L.T("sync_done_none");
        }
        catch (Exception ex)
        {
            _status.Text = prevStatus;
            MessageBox.Show(this, L.T("sync_fail") + ex.Message + L.T("sync_fail_hint"), L.T("err"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _syncing = false;
            _btnSync.Enabled = true;
        }
    }

    /// <summary>메일 원본을 보관 폴더의 연도별 mbox로 정리 (원본 파일은 남겨 둠).</summary>
    private void RunSplit()
    {
        if (_syncing) return;
        MboxStore.SplitPlan plan;
        try { plan = MboxStore.PlanSplit(_dbPath); }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.T("split_fail") + ex.Message, L.T("err"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (plan.Count == 0)
        {
            MessageBox.Show(this, L.F("split_done_none", plan.ArchiveDir), L.T("split_title"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (plan.MissingFiles.Count > 0)
        {
            MessageBox.Show(this, L.F("split_missing", string.Join("\n", plan.MissingFiles)), L.T("err"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (MessageBox.Show(this, L.F("split_confirm", plan.Count, plan.ArchiveDir, plan.Bytes / 1048576 + 1),
                L.T("split_title"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        _syncing = true; // 동기화와 동시에 돌지 않게
        _btnSync.Enabled = _btnSplit.Enabled = false;
        try
        {
            _db.Dispose();
            int total = plan.Count;
            var result = ImportUi.RunWithProgress(this, L.T("split_title"),
                report => MboxStore.SplitByYear(_dbPath, n => report(L.F("split_progress", n, total))));
            string msg = L.F("split_done", result.Moved, result.ArchiveDir);
            if (result.UnusedFiles.Count > 0)
                msg += L.F("split_unused", string.Join("\n", result.UnusedFiles));
            MessageBox.Show(this, msg, L.T("split_title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.T("split_fail") + ex.Message, L.T("err"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _db = new MailDb(_dbPath);
            _syncing = false;
            _btnSync.Enabled = _btnSplit.Enabled = true;
        }
        ClearDetail();
        RunSearch();
    }

    /// <summary>백업 폴더 고르기. 취소하면 null.</summary>
    private string? PickBackupDir(string? current)
    {
        while (true)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = L.T("backup_pick"),
                UseDescriptionForTitle = true,
                SelectedPath = current ?? "",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return null;
            if (Backup.IsValidTarget(_dbPath, dlg.SelectedPath)) return dlg.SelectedPath;
            MessageBox.Show(this, L.T("backup_bad_target"), L.T("err"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            current = dlg.SelectedPath;
        }
    }

    /// <summary>DB와 메일 원본을 백업 폴더로 복사 (바뀐 파일만).</summary>
    private void RunBackup()
    {
        if (_syncing) return;
        string? target = Backup.GetTarget(_dbPath);
        if (target is null)
        {
            target = PickBackupDir(null);
            if (target is null) return;
        }
        else
        {
            // 예 = 백업, 아니요 = 위치 바꾸기
            var answer = MessageBox.Show(this, L.F("backup_confirm", target, _db.GetMeta("backup_last_time") ?? L.T("backup_never")),
                L.T("backup_title"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return;
            if (answer == DialogResult.No)
            {
                target = PickBackupDir(target);
                if (target is null) return;
            }
        }

        _syncing = true; // 동기화·정리와 동시에 돌지 않게
        _btnSync.Enabled = _btnSplit.Enabled = _btnBackup.Enabled = false;
        try
        {
            Backup.SetTarget(_dbPath, target);
            string dir = target;
            var r = ImportUi.RunWithProgress(this, L.T("backup_title"), report => Backup.Run(_dbPath, dir, report));
            string msg = L.F("backup_done", r.TargetDir, r.Copied, r.CopiedBytes / 1048576.0, r.Unchanged);
            if (r.Outside.Count > 0) msg += L.F("backup_outside", string.Join("\n", r.Outside));
            MessageBox.Show(this, msg, L.T("backup_title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.T("backup_fail") + ex.Message, L.T("err"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _syncing = false;
            _btnSync.Enabled = _btnSplit.Enabled = _btnBackup.Enabled = true;
        }
    }

    private void Reimport()
    {
        using var dlg = new OpenFileDialog
        {
            Title = L.T("import_title"),
            Filter = L.T("import_filter"),
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _db.Dispose();
            int n = ImportUi.Run(this, dlg.FileNames, _dbPath);
            MessageBox.Show(this, L.F("import_done", n), L.T("import_done_title"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.T("import_fail") + ex.Message, L.T("err"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _db = new MailDb(_dbPath);
        }

        ClearDetail();
        RefreshStats();
        RunSearch();
    }
}
