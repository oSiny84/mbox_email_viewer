using System.Text.Json;

namespace MailViewer;

/// <summary>환경설정 창: 언어 + Gmail 계정 (Google OAuth 로그인 또는 앱 비밀번호).</summary>
public sealed class SettingsForm : Form
{
    private readonly ComboBox _cboLang = new();
    private readonly RadioButton _rdoOAuth = new();
    private readonly RadioButton _rdoPass = new();
    private readonly TextBox _txtUser = new();
    private readonly Panel _pnlOAuth = new();
    private readonly Panel _pnlPass = new();
    private readonly TextBox _txtClientId = new();
    private readonly TextBox _txtClientSecret = new();
    private readonly Button _btnSignIn = new();
    private readonly Label _lblOAuthStatus = new();
    private readonly TextBox _txtPass = new();
    private readonly string _dbPath;

    // 로그인으로 받은(또는 저장돼 있던) 리프레시 토큰. 클라이언트 ID/보안 비밀이 바뀌면 무효.
    private string? _refreshToken;
    private CancellationTokenSource? _signInCts;

    public SettingsForm(string dbPath)
    {
        _dbPath = dbPath;
        Text = L.T("set_title");
        Width = 540;
        Height = 520;
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
            Left = 12, Top = 52, Width = 500, Height = 370,
        };

        _rdoOAuth.Text = L.T("set_mode_oauth");
        _rdoOAuth.SetBounds(10, 24, 230, 26);
        _rdoPass.Text = L.T("set_mode_pass");
        _rdoPass.SetBounds(250, 24, 230, 26);

        var lblUser = new Label { Text = L.T("set_user"), Left = 10, Top = 62, Width = 110 };
        _txtUser.SetBounds(125, 58, 355, 26);

        // ── OAuth 패널 ──
        _pnlOAuth.SetBounds(0, 94, 495, 270);
        var oauthGuide = new Label { Left = 10, Top = 0, Width = 475, Height = 130, Text = L.T("set_oauth_guide") };
        var lblClientId = new Label { Text = L.T("set_client_id"), Left = 10, Top = 138, Width = 110 };
        _txtClientId.SetBounds(125, 134, 355, 26);
        var lblClientSecret = new Label { Text = L.T("set_client_secret"), Left = 10, Top = 172, Width = 110 };
        _txtClientSecret.SetBounds(125, 168, 355, 26);
        _txtClientSecret.UseSystemPasswordChar = true;
        var btnJson = new Button { Text = L.T("set_load_json"), Left = 125, Top = 204, Width = 130, Height = 32 };
        btnJson.Click += (_, _) => LoadClientJson();
        _btnSignIn.Text = L.T("set_signin");
        _btnSignIn.SetBounds(265, 204, 215, 32);
        _btnSignIn.Click += async (_, _) => await SignInAsync();
        _lblOAuthStatus.SetBounds(10, 242, 470, 24);
        _lblOAuthStatus.AutoEllipsis = true;
        _pnlOAuth.Controls.AddRange(new Control[]
            { oauthGuide, lblClientId, _txtClientId, lblClientSecret, _txtClientSecret, btnJson, _btnSignIn, _lblOAuthStatus });

        // ── 앱 비밀번호 패널 ──
        _pnlPass.SetBounds(0, 94, 495, 270);
        var passGuide = new Label { Left = 10, Top = 0, Width = 475, Height = 120, Text = L.T("set_guide") };
        var lblPass = new Label { Text = L.T("set_pass"), Left = 10, Top = 132, Width = 110 };
        _txtPass.SetBounds(125, 128, 355, 26);
        _txtPass.UseSystemPasswordChar = true;
        var btnTest = new Button { Text = L.T("set_test"), Left = 125, Top = 170, Width = 130, Height = 32 };
        btnTest.Click += (_, _) =>
        {
            if (!ValidateUser(out var user) || !ValidatePass(out var pass)) return;
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
        _pnlPass.Controls.AddRange(new Control[] { passGuide, lblPass, _txtPass, btnTest });

        grp.Controls.AddRange(new Control[] { _rdoOAuth, _rdoPass, lblUser, _txtUser, _pnlOAuth, _pnlPass });

        var btnSave = new Button { Text = L.T("set_save"), Left = 300, Top = 434, Width = 100, Height = 32 };
        var btnCancel = new Button { Text = L.T("set_cancel"), Left = 410, Top = 434, Width = 100, Height = 32, DialogResult = DialogResult.Cancel };
        btnSave.Click += (_, _) => Save();

        // ── 기존 값 채우기 (이벤트 연결 전) ──
        var (clientId, secret) = GmailSync.LoadOAuthClient(dbPath);
        _txtClientId.Text = clientId;
        _txtClientSecret.Text = secret;
        var existing = GmailSync.LoadAccount(dbPath);
        if (existing is not null) _txtUser.Text = existing.User;
        if (existing?.OAuth is { } o)
        {
            _refreshToken = o.RefreshToken;
            _lblOAuthStatus.Text = L.F("set_oauth_saved", existing.User);
        }
        else
        {
            _lblOAuthStatus.Text = L.T("set_oauth_none");
        }
        if (existing is not null && existing.OAuth is null) _txtPass.Text = existing.AppPassword;
        // 앱 비밀번호로 이미 쓰던 사용자는 그대로, 신규는 OAuth(2단계 인증 불필요)를 기본으로
        bool oauthMode = existing is null || existing.OAuth is not null;
        _rdoOAuth.Checked = oauthMode;
        _rdoPass.Checked = !oauthMode;
        UpdateMode();

        _rdoOAuth.CheckedChanged += (_, _) => UpdateMode();
        // 리프레시 토큰은 발급받은 클라이언트에 묶여 있으므로 클라이언트가 바뀌면 다시 로그인해야 함
        EventHandler clientChanged = (_, _) =>
        {
            if (_refreshToken is null) return;
            _refreshToken = null;
            _lblOAuthStatus.Text = L.T("set_oauth_changed");
        };
        _txtClientId.TextChanged += clientChanged;
        _txtClientSecret.TextChanged += clientChanged;

        FormClosing += (_, _) => _signInCts?.Cancel();

        Controls.AddRange(new Control[] { lblLang, _cboLang, grp, btnSave, btnCancel });
        CancelButton = btnCancel;
        AcceptButton = btnSave;
    }

    private void UpdateMode()
    {
        _pnlOAuth.Visible = _rdoOAuth.Checked;
        _pnlPass.Visible = !_rdoOAuth.Checked;
    }

    private async Task SignInAsync()
    {
        string clientId = _txtClientId.Text.Trim();
        string secret = _txtClientSecret.Text.Trim();
        if (clientId.Length == 0 || secret.Length == 0)
        {
            MessageBox.Show(this, L.T("set_need_client"), L.T("confirm"));
            return;
        }

        _btnSignIn.Enabled = false;
        _lblOAuthStatus.Text = L.T("set_oauth_wait");
        _signInCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            var result = await GoogleOAuth.SignInAsync(clientId, secret, _signInCts.Token);
            if (IsDisposed) return;
            string user = result.Email.Length > 0 ? result.Email : _txtUser.Text.Trim();

            // 받은 토큰으로 IMAP 로그인까지 확인 (관리자 차단/IMAP 꺼짐을 여기서 바로 알 수 있게)
            UseWaitCursor = true;
            await Task.Run(() =>
            {
                string token = GoogleOAuth.GetAccessToken(clientId, secret, result.RefreshToken);
                GmailSync.TestConnectionOAuth(user, token);
            });
            if (IsDisposed) return;

            _txtUser.Text = user;
            _refreshToken = result.RefreshToken;
            _lblOAuthStatus.Text = L.F("set_oauth_ok", user);
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed) _lblOAuthStatus.Text = L.T("set_oauth_cancel");
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            _lblOAuthStatus.Text = L.T("set_oauth_none");
            MessageBox.Show(this, L.T("set_oauth_fail") + ex.Message, L.T("set_test_title"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (!IsDisposed)
            {
                UseWaitCursor = false;
                _btnSignIn.Enabled = true;
            }
            _signInCts?.Dispose();
            _signInCts = null;
        }
    }

    /// <summary>Google Cloud 콘솔에서 내려받은 OAuth 클라이언트 JSON에서 ID/보안 비밀 읽기.</summary>
    private void LoadClientJson()
    {
        using var dlg = new OpenFileDialog { Filter = L.T("set_json_filter") };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(dlg.FileName));
            var root = doc.RootElement;
            // 데스크톱 앱은 "installed", 웹 앱 형식이면 "web" 아래에 있다
            JsonElement c = root.TryGetProperty("installed", out var i) ? i
                          : root.TryGetProperty("web", out var w) ? w : root;
            string id = c.TryGetProperty("client_id", out var a) ? a.GetString() ?? "" : "";
            string sec = c.TryGetProperty("client_secret", out var b) ? b.GetString() ?? "" : "";
            if (id.Length == 0 || sec.Length == 0) throw new FormatException();
            _txtClientId.Text = id;
            _txtClientSecret.Text = sec;
        }
        catch (Exception)
        {
            MessageBox.Show(this, L.T("set_json_bad"), L.T("err"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Save()
    {
        // 언어는 항상 저장
        L.Save(_cboLang.SelectedIndex == 1 ? "ko" : "en");

        if (_rdoOAuth.Checked)
        {
            string clientId = _txtClientId.Text.Trim();
            string secret = _txtClientSecret.Text.Trim();
            if (_refreshToken is null)
            {
                // 아무것도 입력 안 했으면 언어만 저장
                if (clientId.Length > 0 || secret.Length > 0)
                {
                    MessageBox.Show(this, L.T("set_need_signin"), L.T("confirm"));
                    return;
                }
            }
            else
            {
                if (!ValidateUser(out var user)) return;
                GmailSync.SaveOAuthAccount(_dbPath, user, new GmailSync.OAuthConfig(clientId, secret, _refreshToken));
            }
        }
        else
        {
            // Gmail 계정은 입력돼 있을 때만 저장 (언어만 바꾸는 경우 허용)
            string user = _txtUser.Text.Trim();
            string pass = _txtPass.Text.Replace(" ", "");
            if (user.Length > 0 || pass.Length > 0)
            {
                if (!ValidateUser(out user) || !ValidatePass(out pass)) return;
                GmailSync.SaveAccount(_dbPath, user, pass);
            }
        }
        DialogResult = DialogResult.OK;
    }

    private bool ValidateUser(out string user)
    {
        user = _txtUser.Text.Trim();
        if (user.Length == 0 || !user.Contains('@'))
        {
            MessageBox.Show(this, L.T("set_need_user"), L.T("confirm"));
            return false;
        }
        return true;
    }

    private bool ValidatePass(out string pass)
    {
        pass = _txtPass.Text.Replace(" ", ""); // 구글이 4자리씩 띄워 보여주는 공백 허용
        if (pass.Length == 0)
        {
            MessageBox.Show(this, L.T("set_need_pass"), L.T("confirm"));
            return false;
        }
        return true;
    }
}
