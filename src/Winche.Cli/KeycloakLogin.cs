using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Winche.Cli;

/// <summary>
/// Signs in with the console's own Keycloak client (authorization code + PKCE, RFC 8252 loopback
/// redirect), the same client the console's web UI uses. The client must allow the redirect URI
/// <c>http://127.0.0.1:{port}/callback</c> (default port 9005).
/// </summary>
internal sealed class KeycloakLogin(CliEnvironment env, HttpClient http)
{
    public const int DefaultPort = 9005;

    internal sealed record AuthConfig(
        [property: JsonPropertyName("provider")] string Provider,
        [property: JsonPropertyName("authority")] string? Authority,
        [property: JsonPropertyName("clientId")] string? ClientId,
        [property: JsonPropertyName("scopes")] string? Scopes);

    private sealed record Discovery(
        [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
        [property: JsonPropertyName("token_endpoint")] string TokenEndpoint);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    public async Task<AuthConfig> GetAuthConfigAsync(Uri console, CancellationToken ct)
    {
        var config = await http.GetFromJsonAsync<AuthConfig>(new Uri(console, "api/auth/config"), ct)
                     ?? throw new CliException("The console sent no auth config.");
        if (config.Provider != "keycloak" || config.Authority is null || config.ClientId is null)
            throw new CliException(
                "This console signs in with its own user accounts (Identity mode); the CLI needs a console in " +
                "Keycloak mode. Pass a bearer token with --token or WINCHE_TOKEN instead.");
        return config;
    }

    public async Task<SavedLogin> LoginAsync(Uri console, int port, CancellationToken ct)
    {
        var config = await GetAuthConfigAsync(console, ct);
        var discovery = await DiscoverAsync(config.Authority!, ct);

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var redirect = $"http://127.0.0.1:{port}/callback";
        var scopes = string.IsNullOrWhiteSpace(config.Scopes) ? "openid" : config.Scopes;

        var url = $"{discovery.AuthorizationEndpoint}?response_type=code" +
                  $"&client_id={Uri.EscapeDataString(config.ClientId!)}" +
                  $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
                  $"&scope={Uri.EscapeDataString(scopes)}" +
                  $"&state={state}&code_challenge={challenge}&code_challenge_method=S256";

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new CliException($"Can't listen on 127.0.0.1:{port} for the sign-in callback ({ex.Message}). Try --port.");
        }

        env.Out.WriteLine("Opening your browser to sign in. If it doesn't open, visit:");
        env.Out.WriteLine(url);
        env.OpenBrowser(url);

        var code = await WaitForCodeAsync(listener, state, ct);
        var token = await PostTokenAsync(discovery.TokenEndpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = config.ClientId!,
            ["code"] = code,
            ["redirect_uri"] = redirect,
            ["code_verifier"] = verifier,
        }, ct);

        return ToLogin(token, discovery.TokenEndpoint, config.ClientId!);
    }

    /// <summary>A fresh access token for <paramref name="login"/>, refreshing it when it expires within a minute.</summary>
    public async Task<SavedLogin?> RefreshIfNeededAsync(SavedLogin login, CancellationToken ct)
    {
        if (login.ExpiresAt > env.Time.GetUtcNow().AddMinutes(1)) return login;
        if (login.RefreshToken is null) return null;
        try
        {
            var token = await PostTokenAsync(login.TokenEndpoint, new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = login.ClientId,
                ["refresh_token"] = login.RefreshToken,
            }, ct);
            return ToLogin(token, login.TokenEndpoint, login.ClientId);
        }
        catch (CliException)
        {
            return null;   // the session ended; sign in again
        }
    }

    private SavedLogin ToLogin(TokenResponse token, string tokenEndpoint, string clientId) => new()
    {
        TokenEndpoint = tokenEndpoint,
        ClientId = clientId,
        AccessToken = token.AccessToken,
        RefreshToken = token.RefreshToken,
        ExpiresAt = env.Time.GetUtcNow().AddSeconds(token.ExpiresIn),
        User = Jwt.Claim(token.AccessToken, "email") ?? Jwt.Claim(token.AccessToken, "preferred_username"),
    };

    private async Task<Discovery> DiscoverAsync(string authority, CancellationToken ct)
    {
        var url = authority.TrimEnd('/') + "/.well-known/openid-configuration";
        try
        {
            return await http.GetFromJsonAsync<Discovery>(url, ct) ?? throw new CliException($"No OpenID configuration at {url}.");
        }
        catch (HttpRequestException ex)
        {
            throw new CliException($"Can't reach Keycloak at {url} ({ex.Message}).");
        }
    }

    private async Task<TokenResponse> PostTokenAsync(string endpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await http.PostAsync(endpoint, new FormUrlEncodedContent(form), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new CliException($"Sign-in failed: {(int)response.StatusCode} {Describe(body)}");
        return JsonSerializer.Deserialize<TokenResponse>(body) ?? throw new CliException("Sign-in failed: empty token response.");
    }

    private static string Describe(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error_description", out var d)) return d.GetString() ?? body;
            if (doc.RootElement.TryGetProperty("error", out var e)) return e.GetString() ?? body;
        }
        catch (JsonException) { }
        return body;
    }

    private static async Task<string> WaitForCodeAsync(HttpListener listener, string state, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        while (true)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new CliException("Timed out waiting for the browser sign-in.");
            }

            if (ctx.Request.Url?.AbsolutePath != "/callback")
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                continue;
            }

            var query = System.Web.HttpUtility.ParseQueryString(ctx.Request.Url.Query);
            var error = query["error"];
            var ok = error is null && query["state"] == state && query["code"] is not null;
            await RespondAsync(ctx, ok
                ? "Signed in to the Winche CLI. You can close this window."
                : $"Sign-in failed{(error is null ? "" : $": {query["error_description"] ?? error}")}. You can close this window.");

            if (error is not null) throw new CliException($"Sign-in failed: {query["error_description"] ?? error}");
            if (query["state"] != state) throw new CliException("Sign-in failed: the callback's state didn't match.");
            return query["code"] ?? throw new CliException("Sign-in failed: no code in the callback.");
        }
    }

    private static async Task RespondAsync(HttpListenerContext ctx, string message)
    {
        var html = Encoding.UTF8.GetBytes(
            $"<!doctype html><meta charset=utf-8><title>Winche CLI</title>" +
            $"<body style=\"font-family:system-ui;margin:3rem\"><p>{WebUtility.HtmlEncode(message)}</p></body>");
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = html.Length;
        await ctx.Response.OutputStream.WriteAsync(html);
        ctx.Response.Close();
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal static class Jwt
{
    /// <summary>Reads one string claim from a JWT's payload without validating it (display only).</summary>
    public static string? Claim(string token, string name)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}
