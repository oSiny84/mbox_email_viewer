using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MailViewer;

/// <summary>Google OAuth 2.0 (설치형 앱 / 루프백 리디렉트 + PKCE).
/// 2단계 인증·앱 비밀번호 없이 회사(Workspace) 계정 로그인 그대로 IMAP 접근 토큰을 얻는다.
/// WinForms 비의존 — UI는 SettingsForm, 사용처는 GmailSync.</summary>
public static class GoogleOAuth
{
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    // IMAP 전체 접근 + 로그인한 주소 확인용(openid email)
    private const string Scope = "https://mail.google.com/ openid email";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public record SignInResult(string Email, string RefreshToken);

    /// <summary>토큰 엔드포인트 오류. invalid_grant = 리프레시 토큰 만료/취소 → 다시 로그인 필요.</summary>
    public sealed class OAuthException(string error, string message) : Exception(message)
    {
        public string Error { get; } = error;
        public bool NeedsReLogin => Error == "invalid_grant";
    }

    /// <summary>브라우저로 Google 로그인 → 루프백으로 인증 코드 수신 → 토큰 교환.</summary>
    public static async Task<SignInResult> SignInAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));

        // TcpListener: HttpListener(HTTP.sys)와 달리 URL ACL/관리자 권한 문제가 없다
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string redirectUri = $"http://127.0.0.1:{port}/";

            string url = AuthEndpoint +
                "?response_type=code" +
                "&client_id=" + Uri.EscapeDataString(clientId) +
                "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
                "&scope=" + Uri.EscapeDataString(Scope) +
                "&code_challenge=" + challenge +
                "&code_challenge_method=S256" +
                "&state=" + state +
                // offline + consent: 재로그인 때도 항상 리프레시 토큰을 받기 위해 필수
                "&access_type=offline&prompt=consent";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            var query = await WaitForRedirectAsync(listener, ct);
            if (query.TryGetValue("error", out var err))
                throw new OAuthException(err, L.T("oauth_denied") + err);
            if (!query.TryGetValue("state", out var gotState) || gotState != state)
                throw new OAuthException("state_mismatch", L.T("oauth_state"));
            string code = query["code"];

            var json = await PostTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
            }, ct);

            string refresh = json.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
            if (refresh.Length == 0)
                throw new OAuthException("no_refresh_token", L.T("oauth_no_refresh"));
            string email = json.TryGetProperty("id_token", out var idt) ? EmailFromIdToken(idt.GetString() ?? "") : "";
            return new SignInResult(email, refresh);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>리프레시 토큰 → 액세스 토큰 (약 1시간 유효). 동기화 시작마다 호출.</summary>
    public static string GetAccessToken(string clientId, string clientSecret, string refreshToken)
    {
        var json = PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
        }, CancellationToken.None).GetAwaiter().GetResult();
        return json.GetProperty("access_token").GetString()
            ?? throw new OAuthException("no_access_token", "no access_token");
    }

    private static async Task<JsonElement> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        // ConfigureAwait(false): GetAccessToken이 UI 스레드에서 동기 대기해도 교착되지 않게
        using var resp = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonElement json;
        try { json = JsonDocument.Parse(body).RootElement.Clone(); }
        catch (JsonException) { throw new OAuthException("bad_response", $"HTTP {(int)resp.StatusCode}: {body}"); }

        if (!resp.IsSuccessStatusCode || json.TryGetProperty("error", out _))
        {
            string error = json.TryGetProperty("error", out var e) ? e.GetString() ?? "" : $"http_{(int)resp.StatusCode}";
            string desc = json.TryGetProperty("error_description", out var d) ? d.GetString() ?? "" : "";
            string msg = $"{error}{(desc.Length > 0 ? ": " + desc : "")}";
            if (error == "invalid_grant") msg += "\n" + L.T("oauth_relogin");
            throw new OAuthException(error, msg);
        }
        return json;
    }

    /// <summary>루프백 연결을 받아 code= 또는 error=가 담긴 요청이 올 때까지 대기.
    /// 브라우저가 /favicon.ico 등을 따로 요청하므로 그런 요청은 404로 돌려보낸다.</summary>
    private static async Task<Dictionary<string, string>> WaitForRedirectAsync(TcpListener listener, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            using var stream = client.GetStream();

            // 크롬 등의 예비 연결(아무것도 안 보냄)에 붙잡히지 않도록 연결별 읽기 시간 제한
            string requestLine;
            using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                readCts.CancelAfter(TimeSpan.FromSeconds(3));
                try { requestLine = await ReadRequestLineAsync(stream, readCts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
                catch (IOException) { continue; }
            }

            // "GET /?code=...&state=... HTTP/1.1"
            var parts = requestLine.Split(' ');
            string target = parts.Length >= 2 ? parts[1] : "/";
            int q = target.IndexOf('?');
            var query = q >= 0 ? ParseQuery(target[(q + 1)..]) : new Dictionary<string, string>();

            if (!query.ContainsKey("code") && !query.ContainsKey("error"))
            {
                await WriteResponseAsync(stream, "404 Not Found", "", ct);
                continue;
            }

            bool ok = query.ContainsKey("code");
            string title = WebUtility.HtmlEncode(L.T(ok ? "oauth_page_ok" : "oauth_page_fail"));
            string html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>MailViewer</title></head>" +
                          $"<body style=\"font-family:sans-serif;padding:40px\"><h2>{title}</h2>" +
                          $"<p>{WebUtility.HtmlEncode(L.T("oauth_page_close"))}</p></body></html>";
            await WriteResponseAsync(stream, "200 OK", html, ct);
            return query;
        }
    }

    private static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        // 요청 줄만 필요 — 헤더는 읽고 버린다 (빈 줄까지)
        var buf = new byte[8192];
        var sb = new StringBuilder();
        while (!sb.ToString().Contains("\r\n\r\n") && sb.Length < 65536)
        {
            int n = await stream.ReadAsync(buf, ct);
            if (n == 0) break;
            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
        }
        string s = sb.ToString();
        int eol = s.IndexOf("\r\n", StringComparison.Ordinal);
        return eol >= 0 ? s[..eol] : s;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string status, string html, CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(html);
        string head = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\n" +
                      $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private static Dictionary<string, string> ParseQuery(string qs)
    {
        var d = new Dictionary<string, string>();
        foreach (var pair in qs.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string k = Uri.UnescapeDataString((eq >= 0 ? pair[..eq] : pair).Replace('+', ' '));
            string v = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')) : "";
            d[k] = v;
        }
        return d;
    }

    /// <summary>id_token(JWT) 페이로드의 email. 토큰 엔드포인트에서 TLS로 직접 받은 값이라 서명 검증은 생략.</summary>
    private static string EmailFromIdToken(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return "";
        try
        {
            string p = parts[1].Replace('-', '+').Replace('_', '/');
            p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(p));
            return doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    private static string Base64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
