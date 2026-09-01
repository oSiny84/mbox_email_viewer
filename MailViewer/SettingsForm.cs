namespace MailViewer;

/// <summary>환경설정 창: 언어 + Gmail 계정(앱 비밀번호).</summary>
public sealed class SettingsForm : Form
{
    private readonly ComboBox _cboLang = new();
    private readonly TextBox _txtUser = new();
    private readonly TextBox _txtPass = new();
    private readonly string _dbPath;

    public SettingsForm(string dbPath)
    {
        _dbPath = dbPath;
        Text = L.T("set_title");
        Width = 540;
        Height = 430;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("맑은 고딕", 9.5f);

        var lblLang = new Label { Text = L.T("set_lang"), Left = 14, Top = 18, Width = 140 };
        _cboLang.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboLang.Items.AddRange(new object[] { "English", "한국어" });
        _cboLang.SelectedIndex = L.Lang == "ko" ? 1 : 0;
        _cboLang.SetBounds(160, 14, 160, 26);

        var grp = new GroupBox
        {
            Text = L.T("set_gmail_group"),
            Left = 12, Top = 52, Width = 500, Height = 280,
        };

        var guide = new Label
        {
            Left = 10, Top = 22, Width = 480, Height = 120,
            Text = L.T("set_guide"),
        };

        var lblUser = new Label { Text = L.T("set_user"), Left = 10, Top = 152, Width = 110 };
        _txtUser.SetBounds(125, 148, 355, 26);

        var lblPass = new Label { Text = L.T("set_pass"), Left = 10, Top = 188, Width = 110 };
        _txtPass.SetBounds(125, 184, 355, 26);
        _txtPass.UseSystemPasswordChar = true;

        var btnTest = new Button { Text = L.T("set_test"), Left = 125, Top = 226, Width = 130, Height = 32 };
        btnTest.Click += (_, _) =>
        {
            if (!ValidateInput(out var user, out var pass)) return;
            try
            {
                UseWaitCursor = true;
                GmailSync.TestConnection(user, pass);
                MessageBox.Show(this, L.T("set_test_ok"), L.T("set_test_title"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.T("set_test_fail") + ex.Message, L.T("set_test_title"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { UseWaitCursor = false; }
        };

        grp.Controls.AddRange(new Control[] { guide, lblUser, _txtUser, lblPass, _txtPass, btnTest });

        var btnSave = new Button { Text = L.T("set_save"), Left = 300, Top = 344, Width = 100, Height = 32 };
        var btnCancel = new Button { Text = L.T("set_cancel"), Left = 410, Top = 344, Width = 100, Height = 32, DialogResult = DialogResult.Cancel };

        btnSave.Click += (_, _) =>
        {
            // 언어는 항상 저장
            L.Save(_cboLang.SelectedIndex == 1 ? "ko" : "en");

            // Gmail 계정은 입력돼 있을 때만 저장 (언어만 바꾸는 경우 허용)
            string user = _txtUser.Text.Trim();
            string pass = _txtPass.Text.Replace(" ", "");
            if (user.Length > 0 || pass.Length > 0)
            {
                if (!ValidateInput(out user, out pass)) return;
                GmailSync.SaveAccount(_dbPath, user, pass);
            }
            DialogResult = DialogResult.OK;
        };

        var existing = GmailSync.LoadAccount(dbPath);
        if (existing is not null)
        {
            _txtUser.Text = existing.User;
            _txtPass.Text = existing.AppPassword;
        }

        Controls.AddRange(new Control[] { lblLang, _cboLang, grp, btnSave, btnCancel });
        CancelButton = btnCancel;
        AcceptButton = btnSave;
    }

    private bool ValidateInput(out string user, out string pass)
    {
        user = _txtUser.Text.Trim();
        pass = _txtPass.Text.Replace(" ", ""); // 구글이 4자리씩 띄워 보여주는 공백 허용
        if (user.Length == 0 || !user.Contains('@'))
        {
            MessageBox.Show(this, L.T("set_need_user"), L.T("confirm"));
            return false;
        }
        if (pass.Length == 0)
        {
            MessageBox.Show(this, L.T("set_need_pass"), L.T("confirm"));
            return false;
        }
        return true;
    }
}
