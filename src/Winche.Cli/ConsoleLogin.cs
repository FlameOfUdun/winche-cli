using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Winche.Cli;

/// <summary>
/// Signs in to a Winche Server: opens the server's console in the browser, where you sign in
/// (a password or your company sign-in) and allow the CLI; the browser comes back to <c>http://127.0.0.1:{port}/callback</c>
/// with a one-time code, which the CLI trades (with its PKCE verifier) for a session of your account. Its access token
/// is renewed with its refresh token as it runs out; the session is one of your account's like any other, so signing
/// out everywhere, or the account being turned off, ends it.
/// </summary>
internal sealed class ConsoleLogin(CliEnvironment env, HttpClient http)
{
    public const int DefaultPort = 9005;

    /// <summary>How the session reads among the account's (and on the approval page).</summary>
    private static string Name => $"winche CLI on {Environment.MachineName}";

    /// <summary>Signs in at <paramref name="server"/> (the server's console, <see cref="CredentialStore.ServerOf"/>).</summary>
    public async Task<SavedLogin> LoginAsync(Uri server, int port, CancellationToken ct)
    {
        var verifier = WincheBase64Url.Encode(RandomNumberGenerator.GetBytes(32));
        var challenge = WincheBase64Url.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = WincheBase64Url.Encode(RandomNumberGenerator.GetBytes(16));
        var name = Name;
        var url = new Uri(ConsoleAddress.PagesOf(server), $"cli-login?port={port}&state={state}&challenge={challenge}&name={Uri.EscapeDataString(name)}").AbsoluteUri;

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try { listener.Start(); }
        catch (HttpListenerException ex) { throw new CliException($"Can't listen on 127.0.0.1:{port} for the sign-in callback ({ex.Message}). Try --port."); }

        env.Out.WriteLine("Opening your browser to sign in. If it doesn't open, visit:");
        env.Out.WriteLine(url);
        env.OpenBrowser(url);

        var code = await WaitForCodeAsync(listener, state, ct);
        return await SessionAsync(server, "cli/token", new { code, verifier }, "Sign-in failed", ct)
               ?? throw new CliException("Sign-in failed: the server refused the code. Run winche login again.");
    }

    /// <summary>
    /// <c>winche login --no-browser</c> (a server, an SSH session, a container): shows a code to enter at the server's
    /// console in a browser anywhere, and waits until it's allowed there (RFC 8628's device flow, with PKCE).
    /// </summary>
    public async Task<SavedLogin> DeviceLoginAsync(Uri server, CancellationToken ct)
    {
        var verifier = WincheBase64Url.Encode(RandomNumberGenerator.GetBytes(32));
        var challenge = WincheBase64Url.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var started = await PostAsync(server, "cli/device", new { challenge, name = Name }, ct);
        var deviceCode = started["deviceCode"]!.GetValue<string>();
        var userCode = started["userCode"]!.GetValue<string>();
        var interval = TimeSpan.FromSeconds(Math.Max(1, started["interval"]?.GetValue<int>() ?? 2));
        var until = env.Time.GetUtcNow().AddSeconds(started["expiresIn"]?.GetValue<int>() ?? 600);

        env.Out.WriteLine($"On any device, open {new Uri(ConsoleAddress.PagesOf(server), "cli-login?user_code=" + userCode).AbsoluteUri}");
        env.Out.WriteLine($"(or {new Uri(ConsoleAddress.PagesOf(server), "cli-login").AbsoluteUri} and enter the code), sign in, and allow the CLI. Your code: {userCode}");
        env.Out.WriteLine("Waiting…");
        while (true)
        {
            var poll = await PostAsync(server, "cli/device/poll", new { deviceCode }, ct);
            switch (poll["status"]?.GetValue<string>())
            {
                case "approved":
                    var code = poll["code"]!.GetValue<string>();
                    return await SessionAsync(server, "cli/token", new { code, verifier }, "Sign-in failed", ct)
                           ?? throw new CliException("Sign-in failed: the server refused the code. Run winche login again.");
                case "pending" when env.Time.GetUtcNow() < until:
                    await Task.Delay(interval, env.Time, ct);
                    continue;
                default:
                    throw new CliException("The code expired before it was allowed. Run winche login --no-browser again.");
            }
        }
    }

    private async Task<JsonObject> PostAsync(Uri server, string path, object body, CancellationToken ct)
    {
        HttpResponseMessage response;
        try { response = await http.PostAsJsonAsync(new Uri(server, path), body, ct); }
        catch (HttpRequestException ex) { throw new CliException($"Can't reach the server at {server} ({ex.Message})."); }
        using (response)
        {
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct) is { Length: > 0 } t ? t : "{}") as JsonObject ?? [];
            if (!response.IsSuccessStatusCode)
                throw new CliException($"Sign-in failed: {ConsoleClient.ErrorText(json, response.StatusCode)}");
            return json;
        }
    }

    /// <summary>Saves the session <c>winche setup</c> got for the new admin.</summary>
    public static SavedLogin FromSession(JsonNode session) => new()
    {
        AccessToken = session["accessToken"]!.GetValue<string>(),
        RefreshToken = session["refreshToken"]!.GetValue<string>(),
        ExpiresAt = session["expiresAt"]!.GetValue<DateTimeOffset>(),
        User = session["user"]?.GetValue<string>() ?? "",
    };

    /// <summary>The CLI's name, for a session made elsewhere (setup).</summary>
    public static string SessionName => Name;

    /// <summary>The login as it is, or renewed when its access token is about to run out; null when the session has ended.</summary>
    public async Task<SavedLogin?> FreshAsync(Uri server, SavedLogin login, CancellationToken ct)
    {
        if (login.ExpiresAt > env.Time.GetUtcNow().AddMinutes(1)) return login;
        return await SessionAsync(server, "cli/refresh", new { refreshToken = login.RefreshToken, name = Name }, "Renewing your sign-in failed", ct);
    }

    /// <summary>The login renewed (so the server confirms its session is live), or null when it has ended.</summary>
    public Task<SavedLogin?> StillLiveAsync(Uri server, SavedLogin login, CancellationToken ct) =>
        SessionAsync(server, "cli/refresh", new { refreshToken = login.RefreshToken, name = Name }, "Checking your sign-in failed", ct);

    /// <summary>Ends the session at the server (offline: it's just forgotten here).</summary>
    public async Task LogoutAsync(Uri server, SavedLogin login, CancellationToken ct)
    {
        try { using var _ = await http.PostAsJsonAsync(new Uri(server, "cli/logout"), new { refreshToken = login.RefreshToken }, ct); }
        catch (HttpRequestException) { }
    }

    private async Task<SavedLogin?> SessionAsync(Uri server, string path, object body, string failure, CancellationToken ct)
    {
        HttpResponseMessage response;
        try { response = await http.PostAsJsonAsync(new Uri(server, path), body, ct); }
        catch (HttpRequestException ex) { throw new CliException($"Can't reach the server at {server} ({ex.Message})."); }
        using (response)
        {
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct) is { Length: > 0 } t ? t : "{}") as JsonObject ?? [];
            if (response.StatusCode == HttpStatusCode.Unauthorized) return null;   // the session has ended
            if (!response.IsSuccessStatusCode)
                throw new CliException($"{failure}: {json["error"]?["message"]?.GetValue<string>() ?? response.ReasonPhrase}");
            return new SavedLogin
            {
                AccessToken = json["accessToken"]!.GetValue<string>(),
                RefreshToken = json["refreshToken"]!.GetValue<string>(),
                ExpiresAt = json["expiresAt"]!.GetValue<DateTimeOffset>(),
                User = json["user"]?.GetValue<string>() ?? "",
            };
        }
    }

    internal static async Task<string> WaitForCodeAsync(HttpListener listener, string state, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().WaitAsync(timeout.Token); }
            catch (OperationCanceledException) { throw new CliException("Timed out waiting for the browser sign-in."); }

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
}
