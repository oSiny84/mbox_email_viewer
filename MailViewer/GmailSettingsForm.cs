namespace MailViewer;

/// <summary>Gmail 계정(앱 비밀번호) 설정 창.</summary>
public sealed class GmailSettingsForm : Form
{
    private readonly TextBox _txtUser = new();
    private readonly TextBox _txtPass = new();
    private readonly string _dbPath;

    public GmailSettingsForm(string dbPath)
    {
        _dbPath = dbPath;
        Text = "Gmail 계정 설정";
        Width = 520;
        Height = 360;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("맑은 고딕", 9.5f);

        var guide = new Label
        {
            Dock = DockStyle.Top,
            Height = 130,
            Padding = new Padding(12, 10, 12, 0),
            Text = "동기화에는 Google \"앱 비밀번호\"가 필요합니다 (계정 비밀번호 아님).\n\n" +
                   "발급 방법:\n" +
                   "  1. Google 계정에서 2단계 인증을 켭니다\n" +
                   "  2. myaccount.google.com/apppasswords 접속\n" +
                   "  3. 앱 비밀번호 생성 → 16자리 코드를 아래에 입력\n\n" +
                   "비밀번호는 이 PC의 사용자 계정으로 암호화되어 로컬에만 저장됩니다.",
        };

        var lblUser = new Label { Text = "Gmail 주소", Left = 14, Top = 148, Width = 90 };
        _txtUser.SetBounds(110, 144, 370, 26);

        var lblPass = new Label { Text = "앱 비밀번호", Left = 14, Top = 184, Width = 90 };
        _txtPass.SetBounds(110, 180, 370, 26);
        _txtPass.UseSystemPasswordChar = true;

        var btnTest = new Button { Text = "연결 테스트", Left = 110, Top = 224, Width = 110, Height = 32 };
        var btnSave = new Button { Text = "저장", Left = 260, Top = 224, Width = 100, Height = 32 };
        var btnCancel = new Button { Text = "취소", Left = 368, Top = 224, Width = 100, Height = 32, DialogResult = DialogResult.Cancel };

        btnTest.Click += (_, _) =>
        {
            if (!Validate(out var user, out var pass)) return;
            try
            {
                UseWaitCursor = true;
                GmailSync.TestConnection(user, pass);
                MessageBox.Show(this, "연결 성공!", "테스트", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "연결 실패:\n" + ex.Message, "테스트",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { UseWaitCursor = false; }
        };

        btnSave.Click += (_, _) =>
        {
            if (!Validate(out var user, out var pass)) return;
            GmailSync.SaveAccount(_dbPath, user, pass);
            DialogResult = DialogResult.OK;
        };

        var existing = GmailSync.LoadAccount(dbPath);
        if (existing is not null)
        {
            _txtUser.Text = existing.User;
            _txtPass.Text = existing.AppPassword;
        }

        Controls.AddRange(new Control[] { guide, lblUser, _txtUser, lblPass, _txtPass, btnTest, btnSave, btnCancel });
        CancelButton = btnCancel;
        AcceptButton = btnSave;
    }

    private bool Validate(out string user, out string pass)
    {
        user = _txtUser.Text.Trim();
        pass = _txtPass.Text.Replace(" ", ""); // 구글이 4자리씩 띄워 보여주는 공백 허용
        if (user.Length == 0 || !user.Contains('@'))
        {
            MessageBox.Show(this, "Gmail 주소를 입력하세요.", "확인");
            return false;
        }
        if (pass.Length == 0)
        {
            MessageBox.Show(this, "앱 비밀번호를 입력하세요.", "확인");
            return false;
        }
        return true;
    }
}
