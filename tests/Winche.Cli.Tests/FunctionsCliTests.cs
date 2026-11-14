using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Winche.Cli.Tests;

/// <summary>winche functions init / deploy, and the emulator running a functions folder.</summary>
[Collection("postgres")]
public class FunctionsCliTests(PostgresFixture fx) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("winche-cli-functions-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Recorder : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }
        public HttpStatusCode Answer { get; set; } = HttpStatusCode.OK;
        public string Reply { get; set; } = """{ "version": 4, "functions": [{ "name": "hello" }] }""";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(Answer) { Content = new StringContent(Reply, Encoding.UTF8, "application/json") };
        }
    }

    private async Task<(int Exit, string Out, string Err)> Run(HttpMessageHandler http, Func<string, string?>? vars, params string[] args) =>
        await Run(http, vars, _ => { }, args);

    private async Task<(int Exit, string Out, string Err)> Run(HttpMessageHandler http, Func<string, string?>? vars, Action<string> openBrowser, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await CliApp.RunAsync(args, new CliEnvironment
        {
            Out = stdout, Error = stderr, WorkingDirectory = _dir, ConfigDirectory = _dir,
            GetVariable = vars ?? (_ => null), Http = http, OpenBrowser = openBrowser,
        });
        return (exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>A console with its own accounts: its auth config, the CLI sign-in exchange, functions deploy, logout.</summary>
    private sealed class FakeConsole : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Auth, string? Body)> Requests { get; } = [];
        public HttpStatusCode DeployAnswer { get; set; } = HttpStatusCode.OK;
        public string DeployReply { get; set; } = """{ "version": 4, "functions": [{ "name": "hello" }] }""";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method, path, request.Headers.Authorization?.ToString(), body));
            (HttpStatusCode, string) answer = path switch
            {
                "/_console/api/auth/config" => (HttpStatusCode.OK, """{ "provider": "identity" }"""),
                "/_console/api/cli-login/token" => (HttpStatusCode.OK, """{ "token": "wct_0123456789abcdef_secret", "user": "ann@example.com", "role": "Admin", "expiresAt": "2099-01-01T00:00:00Z" }"""),
                "/_console/api/functions/deploy" => (DeployAnswer, DeployReply),
                "/_console/api/tokens/current" => (HttpStatusCode.NoContent, ""),
                _ => (HttpStatusCode.NotFound, "{}"),
            };
            return new HttpResponseMessage(answer.Item1) { Content = new StringContent(answer.Item2, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Init_makes_a_folder_vs_code_understands_and_deploy_sends_it_through_the_console()
    {
        File.WriteAllText(Path.Combine(_dir, "winche.json"), """{ "rules": {}, "console": "https://api.example.com/_console" }""");
        var (exit, output, _) = await Run(new Recorder(), null, "functions", "init");
        Assert.Equal(0, exit);
        foreach (var f in new[] { "index.ts", "deno.json", ".winche/functions.ts", ".vscode/settings.json" })
            Assert.True(File.Exists(Path.Combine(_dir, "functions", f)), f);
        Assert.Contains("@winche/functions", File.ReadAllText(Path.Combine(_dir, "functions", "deno.json")));
        Assert.Equal("functions", JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "winche.json")))!["functions"]!.GetValue<string>());
        Assert.Contains("added \"functions\"", output);
        File.WriteAllText(Path.Combine(_dir, "functions", "notes.md"), "not code");

        // In CI: a CI token from WINCHE_TOKEN.
        var console = new FakeConsole();
        (exit, output, var err) = await Run(console, v => v == "WINCHE_TOKEN" ? "wct_ci" : null, "functions", "deploy", "-m", "From CI");
        Assert.True(exit == 0, err);
        var deploy = console.Requests.Single();
        Assert.Equal(("/_console/api/functions/deploy", "Bearer wct_ci"), (deploy.Path, deploy.Auth));
        var sent = JsonNode.Parse(deploy.Body!)!;
        Assert.Equal(["deno.json", "index.ts"], sent["files"]!.AsObject().Select(kv => kv.Key).Order());   // not .winche/, .vscode/ or notes
        Assert.Equal("From CI", sent["note"]!.GetValue<string>());
        Assert.Contains("Deployed version 4: hello.", output);

        console.DeployAnswer = HttpStatusCode.BadRequest;
        console.DeployReply = """{ "error": { "code": "invalid-argument", "reason": "functions", "message": "The code doesn't build.", "details": { "errors": ["index.ts:3: Expected ';'"] } } }""";
        (exit, _, err) = await Run(console, v => v == "WINCHE_TOKEN" ? "wct_ci" : null, "functions", "deploy");
        Assert.Equal(1, exit);
        Assert.Contains("Not deployed: The code doesn't build.", err);
        Assert.Contains("Expected ';'", err);

        // Signed out, with no token: it says how to sign in.
        (exit, _, err) = await Run(new FakeConsole(), null, "functions", "deploy");
        Assert.Equal(1, exit);
        Assert.Contains("winche login", err);
    }

    [Fact]
    public async Task Login_to_a_console_with_its_own_accounts_is_allowed_in_the_browser_then_deploy_and_logout()
    {
        File.WriteAllText(Path.Combine(_dir, "winche.json"), """{ "rules": {}, "console": "https://api.example.com/_console", "functions": "functions" }""");
        await Run(new Recorder(), null, "functions", "init");
        var console = new FakeConsole();
        var port = FreePort();
        using var browser = new HttpClient();
        string? opened = null;
        void Approve(string url)
        {
            // The approval page is the console's; "allowing" sends the browser back to the CLI with a code.
            opened = url;
            var q = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            _ = browser.GetAsync($"http://127.0.0.1:{q["port"]}/callback?code=the-code&state={q["state"]}");
        }

        var (exit, output, err) = await Run(console, null, Approve, "login", "--port", port.ToString());
        Assert.True(exit == 0, err);
        Assert.Contains("as ann@example.com", output);
        var page = new Uri(opened!);
        Assert.Equal("/_console/cli-login", page.AbsolutePath);
        var q = System.Web.HttpUtility.ParseQueryString(page.Query);
        Assert.Equal((port.ToString(), 43), (q["port"], q["challenge"]!.Length));
        var exchange = JsonNode.Parse(console.Requests.Single(r => r.Path == "/_console/api/cli-login/token").Body!)!;
        Assert.Equal("the-code", exchange["code"]!.GetValue<string>());
        var verifier = exchange["verifier"]!.GetValue<string>();
        Assert.Equal(q["challenge"], WincheBase64Url.Encode(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));

        (exit, _, err) = await Run(console, null, "functions", "deploy");
        Assert.True(exit == 0, err);
        Assert.Equal("Bearer wct_0123456789abcdef_secret", console.Requests.Last(r => r.Path == "/_console/api/functions/deploy").Auth);

        (exit, output, _) = await Run(console, null, "logout");
        Assert.Contains("Signed out", output);
        Assert.Contains(console.Requests, r => r.Method == HttpMethod.Delete && r.Path == "/_console/api/tokens/current" && r.Auth == "Bearer wct_0123456789abcdef_secret");
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task The_emulator_runs_the_functions_folder()
    {
        if (Environment.GetEnvironmentVariable("WINCHE_DENO") is not { Length: > 0 } deno) return;   // needs Deno
        await Run(new HttpClientHandler(), null, "functions", "init");
        File.WriteAllText(Path.Combine(_dir, "winche.json"), """{ "rules": {}, "functions": "functions" }""");
        var (exit, output, err) = await Run(new HttpClientHandler(), v => v == "WINCHE_DENO" ? deno : null,
            "emulators", "exec", "--port", "0", "--pg", fx.ConnectionString, "--data", Path.Combine(_dir, "data"),
            "curl -sf -X POST \"$WINCHE_EMULATOR/functions/v1/call/hello\" -H 'Content-Type: application/json' -d '{\"data\":{\"name\":\"Ann\"}}' | grep -q 'Hello Ann' && exit 7");
        Assert.True(exit == 7, output + "\n" + err);
        Assert.Contains("functions v1: hello (called by apps)", output);
    }
}
