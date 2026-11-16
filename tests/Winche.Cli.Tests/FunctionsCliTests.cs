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
                // An access token that's already run out, so the next command renews it.
                "/_console/api/cli-login/token" => (HttpStatusCode.OK, """{ "accessToken": "a.first.jwt", "refreshToken": "r1", "user": "ann@example.com", "expiresAt": "2000-01-01T00:00:00Z" }"""),
                "/_console/api/cli-login/refresh" => (HttpStatusCode.OK, """{ "accessToken": "a.second.jwt", "refreshToken": "r2", "user": "ann@example.com", "expiresAt": "2099-01-01T00:00:00Z" }"""),
                "/_console/api/cli-login/logout" => (HttpStatusCode.NoContent, ""),
                "/_console/api/functions/deploy" => (DeployAnswer, DeployReply),
                "/_console/api/tokens/current" => (HttpStatusCode.NoContent, ""),
                "/_console/api/functions/secrets" => (HttpStatusCode.OK, """{ "available": true, "secrets": [{ "name": "MAIL_KEY", "isSet": true, "setAt": "2026-11-12T20:00:00Z", "setBy": "ann@example.com", "usedBy": ["sendReport"] }, { "name": "SA_JSON", "isSet": false, "setAt": null, "setBy": null, "usedBy": [] }] }"""),
                "/_console/api/functions/secrets/MAIL_KEY" or "/_console/api/functions/secrets/SA_JSON" => (HttpStatusCode.NoContent, ""),
                "/_console/api/secrets/sources" => (HttpStatusCode.OK, """
                    { "available": true, "appCredentials": false,
                      "sources": [{ "name": "aws-sm", "title": "AWS Secrets Manager", "hint": "a secret's ARN or its name", "key": "secrets.source.aws" },
                                  { "name": "aws-ssm", "title": "AWS Parameter Store", "hint": "a parameter's name", "key": "secrets.source.aws" }],
                      "keys": [{ "name": "secrets.source.aws", "title": "AWS access key", "isSet": false, "setAt": null, "setBy": null, "sources": ["aws-sm", "aws-ssm"],
                                 "fields": [{ "name": "accessKeyId", "title": "Access key ID", "secret": false, "required": true },
                                            { "name": "secretAccessKey", "title": "Secret access key", "secret": true, "required": true },
                                            { "name": "region", "title": "Region", "secret": false, "required": false }] }] }
                    """),
                "/_console/api/secrets/test" => (HttpStatusCode.OK, """{ "ok": false, "problem": "AWS Parameter Store has nothing at /app/none.", "code": "not-found" }"""),
                "/_console/api/secrets/keys/secrets.source.aws" => (HttpStatusCode.NoContent, ""),
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
        Directory.CreateDirectory(Path.Combine(_dir, "functions", "console", "reports"));
        File.WriteAllText(Path.Combine(_dir, "functions", "console", "reports", "index.html"), "<p>Reports</p>");
        File.WriteAllBytes(Path.Combine(_dir, "functions", "console", "reports", "logo.png"), [0x89, 0x50, 0x4E, 0x47]);

        // In CI: a CI token from WINCHE_TOKEN.
        var console = new FakeConsole();
        (exit, output, var err) = await Run(console, v => v == "WINCHE_TOKEN" ? "wct_ci" : null, "functions", "deploy", "-m", "From CI");
        Assert.True(exit == 0, err);
        var deploy = console.Requests.Single();
        Assert.Equal(("/_console/api/functions/deploy", "Bearer wct_ci"), (deploy.Path, deploy.Auth));
        var sent = JsonNode.Parse(deploy.Body!)!;
        Assert.Equal(["console/reports/index.html", "console/reports/logo.png", "deno.json", "index.ts"], sent["files"]!.AsObject().Select(kv => kv.Key).Order());   // not .winche/, .vscode/ or notes
        Assert.Equal("base64:iVBORw==", sent["files"]!["console/reports/logo.png"]!.GetValue<string>());
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
        Assert.Equal("r1", JsonNode.Parse(console.Requests.Single(r => r.Path == "/_console/api/cli-login/refresh").Body!)!["refreshToken"]!.GetValue<string>());
        Assert.Equal("Bearer a.second.jwt", console.Requests.Last(r => r.Path == "/_console/api/functions/deploy").Auth);

        (exit, output, _) = await Run(console, null, "logout");
        Assert.Contains("Signed ann@example.com out", output);
        Assert.Equal("r2", JsonNode.Parse(console.Requests.Single(r => r.Path == "/_console/api/cli-login/logout").Body!)!["refreshToken"]!.GetValue<string>());
    }

    [Fact]
    public async Task Function_secrets_are_listed_set_from_a_variable_or_a_file_and_removed()
    {
        File.WriteAllText(Path.Combine(_dir, "winche.json"), """{ "rules": {}, "console": "https://api.example.com/_console" }""");
        string? Ci(string v) => v == "WINCHE_TOKEN" ? "wct_ci" : null;
        var console = new FakeConsole();

        var (exit, output, err) = await Run(console, Ci, "functions", "secrets");
        Assert.True(exit == 0, err);
        Assert.Contains("MAIL_KEY", output);
        Assert.Contains("set 2026-11-12 by ann@example.com", output);
        Assert.Contains("used by sendReport", output);
        Assert.Contains("NOT SET", output);

        (exit, output, err) = await Run(console, v => v == "WINCHE_SECRET" ? "k-123" : Ci(v), "functions", "secrets:set", "MAIL_KEY");
        Assert.True(exit == 0, err);
        var put = console.Requests.Last();
        Assert.Equal((HttpMethod.Put, "/_console/api/functions/secrets/MAIL_KEY", "k-123"), (put.Method, put.Path, JsonNode.Parse(put.Body!)!["value"]!.GetValue<string>()));
        Assert.Contains("Set MAIL_KEY.", output);

        File.WriteAllText(Path.Combine(_dir, "sa.json"), """{ "type": "service_account" }""");
        (exit, _, err) = await Run(console, Ci, "functions", "secrets:set", "SA_JSON", "--from-file", "sa.json");
        Assert.True(exit == 0, err);
        Assert.Contains("service_account", JsonNode.Parse(console.Requests.Last().Body!)!["value"]!.GetValue<string>());

        (exit, _, err) = await Run(console, Ci, "functions", "secrets:set", "MAIL_KEY");   // no value and no terminal
        Assert.Equal(2, exit);
        Assert.Contains("WINCHE_SECRET", err);
        (exit, _, err) = await Run(console, Ci, "functions", "secrets:set", "mail-key");
        Assert.Equal(2, exit);
        Assert.Contains("isn't a secret name", err);

        (exit, output, err) = await Run(console, Ci, "functions", "secrets:remove", "MAIL_KEY");
        Assert.True(exit == 0, err);
        Assert.Equal((HttpMethod.Delete, "/_console/api/functions/secrets/MAIL_KEY"), (console.Requests.Last().Method, console.Requests.Last().Path));
        (exit, _, err) = await Run(console, Ci, "functions", "secrets:remove", "NOPE");
        Assert.Equal(1, exit);
        Assert.Contains("There's no secret NOPE", err);
    }

    [Fact]
    public async Task Secrets_can_be_references_to_aws_tested_and_read_with_the_projects_own_key()
    {
        File.WriteAllText(Path.Combine(_dir, "winche.json"), """{ "rules": {}, "console": "https://api.example.com/_console" }""");
        string? Ci(string v) => v == "WINCHE_TOKEN" ? "wct_ci" : null;
        var console = new FakeConsole();

        var (exit, output, err) = await Run(console, Ci, "functions", "secrets:set", "MAIL_KEY", "--ref", "aws-ssm:/app/mail#key");
        Assert.True(exit == 0, err);
        var put = JsonNode.Parse(console.Requests.Last().Body!)!;
        Assert.Equal("aws-ssm:/app/mail#key", put["reference"]!.GetValue<string>());
        Assert.Null(put["value"]);
        Assert.Contains("read from aws-ssm:/app/mail#key", output);
        (exit, _, err) = await Run(console, Ci, "functions", "secrets:set", "MAIL_KEY", "--ref", "no-source");
        Assert.Equal(2, exit);
        Assert.Contains("isn't a reference", err);

        (exit, output, err) = await Run(console, Ci, "secrets:sources");
        Assert.True(exit == 0, err);
        Assert.Contains("aws-ssm:   AWS Parameter Store", output);
        Assert.Contains("the project's AWS access key: not set", output);

        (exit, _, err) = await Run(console, Ci, "secrets:test", "aws-ssm:/app/none");
        Assert.Equal(1, exit);
        Assert.Contains("has nothing at /app/none", err);

        (exit, _, err) = await Run(console, Ci, "secrets:key", "aws", "--region", "eu-west-2");
        Assert.Equal(2, exit);
        Assert.Contains("--access-key-id <Access key ID> is needed", err);
        (exit, _, err) = await Run(console, Ci, "secrets:key", "aws", "--access-key-id", "AKIDWARDS");
        Assert.Equal(2, exit);
        Assert.Contains("WINCHE_SECRET", err);   // the secret key: never an option
        (exit, output, err) = await Run(console, v => v == "WINCHE_SECRET" ? "s3cr3t" : Ci(v), "secrets:key", "aws", "--access-key-id", "AKIDWARDS", "--region", "eu-west-2");
        Assert.True(exit == 0, err);
        var key = JsonNode.Parse(console.Requests.Last().Body!)!["values"]!;
        Assert.Equal(("AKIDWARDS", "s3cr3t", "eu-west-2"), (key["accessKeyId"]!.GetValue<string>(), key["secretAccessKey"]!.GetValue<string>(), key["region"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("https://api.example.com/_console", "https://api.example.com/_console/")]
    [InlineData("https://api.example.com/p/wards/_console/", "https://api.example.com/_console/")]
    [InlineData("http://localhost:5092/p/a/admin", "http://localhost:5092/admin/")]
    public void One_sign_in_covers_every_project_on_a_server(string console, string server)
    {
        Assert.Equal(server, CredentialStore.ServerOf(new Uri(console)).ToString());
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
        Assert.Contains(".secrets.local", File.ReadAllText(Path.Combine(_dir, "functions", ".gitignore")));

        // Secrets for the emulator come from functions/.secrets.local, which is never deployed.
        File.WriteAllText(Path.Combine(_dir, "functions", ".secrets.local"), "# local only\nMAIL_KEY=\"k-local-1\"\n");
        File.WriteAllText(Path.Combine(_dir, "functions", "index.ts"), """
            import { callable } from "@winche/functions";
            export const keyed = callable(async (_d, ctx) => ({ key: ctx.secrets.get("MAIL_KEY") }), { secrets: ["MAIL_KEY"] });
            """);
        Assert.DoesNotContain(".secrets.local", FunctionsFolder.Read(Path.Combine(_dir, "functions")).Keys);
        (exit, output, err) = await Run(new HttpClientHandler(), v => v == "WINCHE_DENO" ? deno : null,
            "emulators", "exec", "--port", "0", "--pg", fx.ConnectionString, "--data", Path.Combine(_dir, "data2"),
            "curl -sf -X POST \"$WINCHE_EMULATOR/functions/v1/call/keyed\" -H 'Content-Type: application/json' -d '{}' | grep -q 'k-local-1' && exit 7");
        Assert.True(exit == 7, output + "\n" + err);
        Assert.Contains("secrets from .secrets.local: MAIL_KEY", output);
    }
}
