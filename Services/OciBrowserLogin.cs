using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using OracleHost.Helpers;

namespace OracleHost.Services;

/// <summary>
/// A captured OCI session (security token + refresh token + session key),
/// persisted in the same layout the OCI CLI and .NET SDK expect:
///   ~/.oci/sessions/&lt;region&gt;/sessions/&lt;id&gt;/{config, token, refresh_token, private_key}
/// </summary>
public class OciSession
{
    public string SessionConfigPath { get; set; } = "";
    public string TokenPath { get; set; } = "";
    public string RefreshTokenPath { get; set; } = "";
    public string KeyPath { get; set; } = "";
    public string UserOcid { get; set; } = "";
    public string TenancyOcid { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Region { get; set; } = "us-ashburn-1";

    public bool IsValid => File.Exists(SessionConfigPath) && File.Exists(TokenPath) && File.Exists(KeyPath);
}

/// <summary>
/// Implements Oracle's browser-based "sign in" flow (OAuth2 authorization-code + PKCE
/// against an identity domain) and stores the captured security token as a CLI-compatible
/// session, so the .NET SDK's SessionTokenAuthenticationDetailsProvider can use it.
/// This mirrors what `oci session authenticate` does.
/// </summary>
public static class OciBrowserLogin
{
    /// <summary>Default loopback callback port, matching the OCI CLI convention.</summary>
    public const int DefaultCallbackPort = 8181;

    public static string AuthorizeEndpoint(string identityDomain) =>
        $"{identityDomain.TrimEnd('/')}/oauth2/v1/authorize";

    public static string TokenEndpoint(string identityDomain) =>
        $"{identityDomain.TrimEnd('/')}/oauth2/v1/token";

    public static string SessionsRoot(string region) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".oci", "sessions", region, "sessions");

    /// <summary>
    /// Locates the most recently modified session config under ~/.oci/sessions,
    /// so sessions created here (or by the OCI CLI) are reused automatically.
    /// </summary>
    public static OciSession? FindExistingSession()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".oci", "sessions");
        if (!Directory.Exists(root)) return null;

        OciSession? best = null;
        foreach (var configPath in Directory.EnumerateFiles(root, "config", SearchOption.AllDirectories))
        {
            try
            {
                var session = LoadSession(configPath);
                if (session == null) continue;
                if (best == null ||
                    File.GetLastWriteTimeUtc(configPath) > File.GetLastWriteTimeUtc(best.SessionConfigPath))
                    best = session;
            }
            catch { /* skip unreadable sessions */ }
        }
        return best;
    }

    /// <summary>
    /// Checks that the local session token exists, is readable, and is not an expired JWT
    /// before startup treats the session as reusable. Opaque tokens are accepted because
    /// their expiry can only be checked by OCI.
    /// </summary>
    internal static bool IsSessionUsable(OciSession session)
    {
        if (!session.IsValid) return false;

        try
        {
            var token = SessionTokenProtector.ReadFile(session.TokenPath).Trim();
            if (string.IsNullOrWhiteSpace(token)) return false;

            var claims = DecodeJwt(token);
            var expiry = claims?["exp"]?.Value<long?>();
            return !expiry.HasValue || DateTimeOffset.UtcNow.ToUnixTimeSeconds() < expiry.Value;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Parses a CLI/SDK-style session config ([DEFAULT] profile).</summary>
    public static OciSession? LoadSession(string configPath)
    {
        if (!File.Exists(configPath)) return null;

        var lines = File.ReadAllLines(configPath);
        string? user = null, tenancy = null, fingerprint = null, region = null, tokenFile = null, keyFile = null;
        bool inDefault = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("[DEFAULT]", StringComparison.OrdinalIgnoreCase)) { inDefault = true; continue; }
            if (line.StartsWith("[") && line.EndsWith("]")) { inDefault = false; continue; }
            if (!inDefault) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim().ToLowerInvariant();
            var value = line[(eq + 1)..].Trim();

            switch (key)
            {
                case "user": user = value; break;
                case "tenancy": tenancy = value; break;
                case "fingerprint": fingerprint = value; break;
                case "region": region = value; break;
                case "security_token_file": tokenFile = value; break;
                case "key_file": keyFile = value; break;
            }
        }

        if (string.IsNullOrEmpty(tokenFile)) return null;

        var dir = Path.GetDirectoryName(configPath) ?? "";
        return new OciSession
        {
            SessionConfigPath = configPath,
            TokenPath = Path.IsPathRooted(tokenFile) ? tokenFile : Path.Combine(dir, tokenFile),
            RefreshTokenPath = Path.Combine(dir, "refresh_token"),
            KeyPath = string.IsNullOrEmpty(keyFile) ? "" :
                Path.IsPathRooted(keyFile) ? keyFile : Path.Combine(dir, keyFile),
            UserOcid = user ?? "",
            TenancyOcid = tenancy ?? "",
            Fingerprint = fingerprint ?? "",
            Region = region ?? "us-ashburn-1"
        };
    }

    /// <summary>
    /// Runs the full browser login flow:
    /// opens the identity-domain OAuth page, captures the auth code on a loopback
    /// callback, exchanges it for a security token + refresh token, and persists a
    /// CLI-compatible session under ~/.oci/sessions/&lt;region&gt;.
    /// </summary>
    public static async Task<OciSession> LoginAsync(
        string identityDomain,
        string clientId,
        string region,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        bool encryptSessionTokens = false)
    {
        identityDomain = NormalizeIdentityDomain(identityDomain);
        clientId = clientId.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("OAuth Client ID is required. Create an OAuth app in your identity domain and paste its client ID.");

        // Generate the session key pair BEFORE the exchange: Oracle binds the returned
        // security token to this key (same as `oci session authenticate`), and the SDK
        // later signs API requests with the matching private key.
        var (privateKeyPem, publicKeyPem, fingerprint) = CryptoHelper.GenerateKeyPair();

        var (port, redirectUri) = ReserveLoopbackPort();
        var state = RandomUrlSafe(32);
        var verifier = RandomUrlSafe(48);
        var challenge = S256(verifier);

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        progress?.Report($"Waiting for login at http://localhost:{port}/ …");

        var authorizeUrl = AuthorizeEndpoint(identityDomain) +
            $"?client_id={Uri.EscapeDataString(clientId)}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            // openid + offline_access gives us the security token and a refresh token.
            // The OAuth app in the identity domain must also have OCI API scopes enabled
            // (see the error raised below when the token lacks user/tenancy OCID claims).
            $"&scope={Uri.EscapeDataString("openid offline_access")}" +
            $"&state={state}" +
            $"&code_challenge={challenge}&code_challenge_method=S256";

        OpenBrowser(authorizeUrl);
        progress?.Report("Browser opened — sign in to Oracle Cloud and approve the app.");

        var code = await WaitForCallbackAsync(listener, state, ct);
        var (accessToken, refreshToken) = await ExchangeCodeAsync(
            identityDomain, clientId, redirectUri, code, verifier, publicKeyPem, ct);

        var claims = DecodeJwt(accessToken);
        var userOcid = GetClaim(claims, "sub", "user_ocid", "user") ?? "";
        var tenancyOcid = GetClaim(claims, "tenancy", "tenant", "tenancy_ocid") ?? "";
        if (string.IsNullOrEmpty(userOcid) || string.IsNullOrEmpty(tenancyOcid))
        {
            throw new InvalidOperationException(
                "Logged in, but the token is missing the " +
                (string.IsNullOrEmpty(userOcid) ? "user" : "tenancy") +
                " OCID claim. This usually means the OAuth app's scopes don't cover the " +
                "OCI API — in the identity domain, make sure the OAuth app has the OCI " +
                "resource scope enabled (or use Oracle's default OCI OAuth app).");
        }

        progress?.Report("Exchanged token — saving session…");

        var session = PersistSession(region, accessToken, refreshToken, userOcid, tenancyOcid,
            privateKeyPem, fingerprint, encryptSessionTokens);
        progress?.Report($"✅ Session saved: {session.SessionConfigPath}");
        return session;
    }

    /// <summary>
    /// Uses a stored refresh token to mint a fresh security token and rewrites the
    /// session's token file. Returns false if the refresh token is missing/expired.
    /// </summary>
    public static async Task<bool> RenewAsync(
        OciSession session,
        string identityDomain,
        string clientId,
        CancellationToken ct = default,
        bool encryptSessionTokens = false)
    {
        if (!File.Exists(session.RefreshTokenPath)) return false;
        var refreshToken = SessionTokenProtector.ReadFile(session.RefreshTokenPath).Trim();
        if (string.IsNullOrEmpty(refreshToken)) return false;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = clientId.Trim(),
                ["scope"] = "openid offline_access"
            });
            var response = await http.PostAsync(TokenEndpoint(identityDomain), content, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;

            var json = JObject.Parse(body);
            var accessToken = json["access_token"]?.ToString();
            var newRefresh = json["refresh_token"]?.ToString();
            if (string.IsNullOrEmpty(accessToken)) return false;

            var protectToken = encryptSessionTokens || SessionTokenProtector.IsProtectedFile(session.TokenPath);
            SessionTokenProtector.WriteFile(session.TokenPath, accessToken, protectToken);
            if (!string.IsNullOrEmpty(newRefresh))
            {
                var protectRefresh = encryptSessionTokens || SessionTokenProtector.IsProtectedFile(session.RefreshTokenPath);
                SessionTokenProtector.WriteFile(session.RefreshTokenPath, newRefresh, protectRefresh);
            }
            return true;
        }
        catch { return false; }
    }

    // ---- internals ----

    private static string NormalizeIdentityDomain(string input)
    {
        var domain = input.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(domain)) throw new ArgumentException("Identity domain URL is required.");
        if (!domain.StartsWith("http://") && !domain.StartsWith("https://"))
            domain = "https://" + domain;
        return domain;
    }

    private static (int Port, string RedirectUri) ReserveLoopbackPort()
    {
        // The OAuth app registers the redirect URI http://localhost:8181/ exactly,
        // so the callback port must be fixed (same convention as the OCI CLI).
        try
        {
            using var probe = new TcpListener(IPAddress.Loopback, DefaultCallbackPort);
            probe.Start();
            return (DefaultCallbackPort, $"http://localhost:{DefaultCallbackPort}/");
        }
        catch
        {
            throw new InvalidOperationException(
                $"Port {DefaultCallbackPort} is in use. Free it (e.g. close another OracleHost/OCI CLI " +
                $"instance) or add an additional redirect URI like http://localhost:{DefaultCallbackPort}/ " +
                $"to your OAuth app.");
        }
    }

    private static void OpenBrowser(string url)
    {
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    private static async Task<string> WaitForCallbackAsync(
        HttpListener listener, string expectedState, CancellationToken ct)
    {
        var timeout = TimeSpan.FromMinutes(5);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            // GetContextAsync has no cancellation support, so race it against
            // the remaining deadline; otherwise an abandoned login hangs forever.
            var contextTask = listener.GetContextAsync();
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            var finished = await Task.WhenAny(contextTask, Task.Delay(remaining, ct));
            if (finished != contextTask)
            {
                // Stop the listener so the orphaned GetContextAsync completes,
                // then observe its result to avoid an unobserved task exception.
                listener.Stop();
                _ = contextTask.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                ct.ThrowIfCancellationRequested();
                break;
            }

            var ctx = await contextTask;
            var query = ctx.Request.QueryString;

            if (query["state"] != expectedState)
            {
                WriteResponse(ctx, "Invalid state — close this tab and retry.", HttpStatusCode.BadRequest);
                continue;
            }

            if (query["error"] != null)
            {
                var description = query["error_description"];
                WriteResponse(ctx, $"Login failed: {query["error"]} {description}".Trim(), HttpStatusCode.BadRequest);
                throw new InvalidOperationException(
                    $"Oracle login failed: {query["error"]} {description}".Trim());
            }

            var code = query["code"];
            if (string.IsNullOrEmpty(code))
            {
                WriteResponse(ctx, "No authorization code received.", HttpStatusCode.BadRequest);
                continue;
            }

            WriteResponse(ctx,
                "✅ Signed in! You can close this tab and return to OracleHost.",
                HttpStatusCode.OK);
            return code;
        }

        throw new TimeoutException("Login timed out after 5 minutes. Try again.");
    }

    private static void WriteResponse(HttpListenerContext ctx, string message, HttpStatusCode status)
    {
        var html = $"<html><body style='font-family:Segoe UI,sans-serif;background:#18181b;color:#e4e4e7;display:flex;align-items:center;justify-content:center;height:100vh;'>" +
                   $"<div style='text-align:center'>{message}</div></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.StatusCode = (int)status;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.OutputStream.Close();
    }

    private static async Task<(string AccessToken, string RefreshToken)> ExchangeCodeAsync(
        string identityDomain, string clientId, string redirectUri, string code, string verifier,
        string publicKeyPem, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
            // Bind the security token to our session key. This mirrors what the OCI CLI's
            // `session authenticate` posts; if Oracle ever changes the field name/format,
            // only this line needs updating.
            ["public_key"] = publicKeyPem
        });

        var response = await http.PostAsync(TokenEndpoint(identityDomain), content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Oracle token exchange failed ({(int)response.StatusCode}): {Truncate(body, 400)}");

        var json = JObject.Parse(body);
        var accessToken = json["access_token"]?.ToString();
        if (string.IsNullOrEmpty(accessToken))
            throw new InvalidOperationException("Oracle returned no access token.");

        return (accessToken, json["refresh_token"]?.ToString() ?? "");
    }

    private static OciSession PersistSession(
        string region, string accessToken, string refreshToken, string userOcid, string tenancyOcid,
        string privateKeyPem, string fingerprint, bool encryptSessionTokens)
    {
        var sessionId = "ocid1.session.oc1.." + Guid.NewGuid().ToString("N");
        var dir = Path.Combine(SessionsRoot(region), sessionId);
        Directory.CreateDirectory(dir);

        var tokenPath = Path.Combine(dir, "token");
        var refreshPath = Path.Combine(dir, "refresh_token");
        var keyPath = Path.Combine(dir, "private_key");
        var configPath = Path.Combine(dir, "config");

        File.WriteAllText(keyPath, privateKeyPem);
        SessionTokenProtector.WriteFile(tokenPath, accessToken, encryptSessionTokens);
        if (!string.IsNullOrEmpty(refreshToken))
            SessionTokenProtector.WriteFile(refreshPath, refreshToken, encryptSessionTokens);

        File.WriteAllText(configPath,
            $"[DEFAULT]\n" +
            $"user={userOcid}\n" +
            $"fingerprint={fingerprint}\n" +
            $"tenancy={tenancyOcid}\n" +
            $"region={region}\n" +
            $"security_token_file={tokenPath}\n" +
            $"key_file={keyPath}\n");

        return new OciSession
        {
            SessionConfigPath = configPath,
            TokenPath = tokenPath,
            RefreshTokenPath = refreshPath,
            KeyPath = keyPath,
            UserOcid = userOcid,
            TenancyOcid = tenancyOcid,
            Fingerprint = fingerprint,
            Region = region
        };
    }

    private static JObject? DecodeJwt(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var bytes = Convert.FromBase64String(payload);
            return JObject.Parse(Encoding.UTF8.GetString(bytes));
        }
        catch { return null; }
    }

    private static string? GetClaim(JObject? claims, params string[] keys)
    {
        if (claims == null) return null;
        foreach (var key in keys)
        {
            var value = claims[key]?.ToString();
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return null;
    }

    private static string RandomUrlSafe(int byteCount) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string S256(string input) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(input)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
