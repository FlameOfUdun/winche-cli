using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Winche.Cli;

/// <summary>
/// Signs in to a console that has its own accounts, like <c>firebase login</c>: opens the console's approval page in
/// the browser, where someone signed in allows the CLI; the browser comes back to <c>http://127.0.0.1:{port}/callback</c>
/// with a one-time code, which the CLI trades (with its PKCE verifier) for a console token acting as them.
/// </summary>
internal sealed class ConsoleLogin(CliEnvironment env, HttpClient http)
{
    public async Task<SavedLogin> LoginAsync(Uri console, int port, CancellationToken ct)
    {
        var verifier = WincheBase64Url.Encode(RandomNumberGenerator.GetBytes(32));
        var challenge = WincheBase64Url.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = WincheBase64Url.Encode(RandomNumberGenerator.GetBytes(16));
        var name = $"winche CLI on {Environment.MachineName}";
        var url = new Uri(console, $"cli-login?port={port}&state={state}&challenge={challenge}&name={Uri.EscapeDataString(name)}").ToString();

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try { listener.Start(); }
        catch (HttpListenerException ex) { throw new CliException($"Can't listen on 127.0.0.1:{port} for the sign-in callback ({ex.Message}). Try --port."); }

        env.Out.WriteLine("Opening your browser to allow the CLI. If it doesn't open, visit:");
        env.Out.WriteLine(url);
        env.OpenBrowser(url);

        var code = await KeycloakLogin.WaitForCodeAsync(listener, state, ct);
        HttpResponseMessage response;
        try { response = await http.PostAsJsonAsync(new Uri(console, "api/cli-login/token"), new { code, verifier }, ct); }
        catch (HttpRequestException ex) { throw new CliException($"Can't reach the console at {console} ({ex.Message})."); }
        using (response)
        {
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct) is { Length: > 0 } t ? t : "{}") as JsonObject ?? [];
            if (!response.IsSuccessStatusCode)
                throw new CliException($"Sign-in failed: {body["error"]?["message"]?.GetValue<string>() ?? response.ReasonPhrase}");
            return new SavedLogin
            {
                Kind = SavedLogin.Console,
                AccessToken = body["token"]!.GetValue<string>(),
                ExpiresAt = body["expiresAt"]!.GetValue<DateTimeOffset>(),
                User = body["user"]?.GetValue<string>(),
            };
        }
    }
}
