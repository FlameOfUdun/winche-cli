using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Winche.Cli;
using Xunit;

namespace Winche.Cli.Tests;

/// <summary>
/// Runs the <c>winche</c> CLI in-process against a host app with the console in Keycloak mode (the same rules API the
/// console's editor uses), with a minted bearer token as <c>WINCHE_TOKEN</c>.
/// </summary>
[Collection("postgres")]
public sealed class CliTests(PostgresFixture fx) : IAsyncLifetime, IDisposable
{
    private const string ConsoleUrl = "http://localhost/_console";
    private readonly string _dir = Directory.CreateTempSubdirectory("winche-cli-").FullName;
    private readonly string _configDir = Directory.CreateTempSubdirectory("winche-cli-config-").FullName;
    private ConsoleAppFactory? _app;

    public Task InitializeAsync() => fx.ResetRulesAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _app?.Dispose();
        Directory.Delete(_dir, recursive: true);
        Directory.Delete(_configDir, recursive: true);
    }

    private ConsoleAppFactory App => _app ??= new ConsoleAppFactory(fx);

    private sealed record Result(int Exit, string Out, string Err);

    private async Task<Result> Run(string? token, HttpMessageHandler? http, Action<string>? openBrowser, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var env = new CliEnvironment
        {
            Out = stdout,
            Error = stderr,
            WorkingDirectory = _dir,
            ConfigDirectory = _configDir,
            GetVariable = name => name == "WINCHE_TOKEN" ? token : null,
            Http = http ?? App.Server.CreateHandler(),
            OpenBrowser = openBrowser ?? (_ => throw new InvalidOperationException("no browser in tests")),
        };
        var exit = await CliApp.RunAsync(args, env);
        return new(exit, stdout.ToString(), stderr.ToString());
    }

    private Task<Result> Admin(params string[] args) => Run(KeycloakTokens.Mint(["Admin"]), null, null, args);

    private void Write(string file, string text) => File.WriteAllText(Path.Combine(_dir, file), text);

    private const string DatabaseRules = """
        match users/{uid} {
          allow read, write: if request.auth.uid == uid;
        }
        """;

    [Fact]
    public async Task Init_then_validate_offline()
    {
        var init = await Run(null, null, null, "init", "--console", ConsoleUrl);
        Assert.Equal(0, init.Exit);
        Assert.True(File.Exists(Path.Combine(_dir, "winche.json")));

        var validate = await Run(null, null, null, "rules", "validate");
        Assert.Equal(0, validate.Exit);
        Assert.Contains("✓ database.rules", validate.Out);
        Assert.Contains("✓ storage.rules", validate.Out);
    }

    [Fact]
    public async Task Validate_reports_file_line_and_column()
    {
        await Run(null, null, null, "init", "--console", ConsoleUrl);
        Write("database.rules", "match users/{uid} {\n  allow read: if request.auth.uid == ;\n}");

        var r = await Run(null, null, null, "rules", "validate");
        Assert.Equal(1, r.Exit);
        Assert.Contains("database.rules:2:", r.Err);
    }

    [Fact]
    public async Task Deploy_saves_a_version_and_hot_swaps_then_is_up_to_date()
    {
        await Run(null, null, null, "init", "--console", ConsoleUrl, "--only", "database");
        Write("database.rules", DatabaseRules);

        var dry = await Admin("rules", "deploy", "--dry-run");
        Assert.Equal(0, dry.Exit);
        Assert.Contains("would deploy database.rules as v1", dry.Out);
        Assert.Contains("+ match users/{uid} {", dry.Out);

        var deploy = await Admin("rules", "deploy", "-m", "first");
        Assert.Equal(0, deploy.Exit);
        Assert.Contains("database: deployed database.rules as v1", deploy.Out);

        var again = await Admin("rules", "deploy");
        Assert.Contains("database: up to date (v1)", again.Out);

        var diff = await Admin("rules", "diff");
        Assert.Contains("database: no differences", diff.Out);

        // The console lists it with the note, and enforces it.
        using var client = App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", KeycloakTokens.Mint(["Admin"]));
        var versions = JsonDocument.Parse(await client.GetStringAsync("/_console/api/rules/database/versions")).RootElement;
        Assert.Equal("first", versions[0].GetProperty("note").GetString());
        var live = JsonDocument.Parse(await client.GetStringAsync("/_console/api/rules/database/live")).RootElement;
        Assert.Contains("users/{uid}", live.GetProperty("rulesJson").GetString());

        // A change deploys as v2.
        Write("database.rules", DatabaseRules.Replace("read, write", "read"));
        var diff2 = await Admin("rules", "diff");
        Assert.Contains("- ", diff2.Out);
        Assert.Contains("+ ", diff2.Out);
        Assert.Contains("database: deployed database.rules as v2", (await Admin("rules", "deploy")).Out);
    }

    [Fact]
    public async Task Invalid_file_deploys_nothing()
    {
        await Run(null, null, null, "init", "--console", ConsoleUrl);
        Write("database.rules", DatabaseRules);
        Write("storage.rules", "match files/{a=**}/{b=**} { allow read; }");

        var r = await Admin("rules", "deploy");
        Assert.Equal(1, r.Exit);
        Assert.DoesNotContain("deployed", r.Out);
        Assert.Contains("storage.rules", r.Err);
    }

    [Fact]
    public async Task Pull_writes_the_live_rules()
    {
        await Run(null, null, null, "init", "--console", ConsoleUrl, "--only", "storage");
        Write("storage.rules", "match public/{f=**} { allow read; }");
        await Admin("rules", "deploy");
        Write("storage.rules", "");

        var pull = await Admin("rules", "pull");
        Assert.Equal(0, pull.Exit);
        Assert.Contains("match public/{f=**}", File.ReadAllText(Path.Combine(_dir, "storage.rules")));
        Assert.Contains("storage: no differences", (await Admin("rules", "diff")).Out);
    }

    [Fact]
    public async Task Non_admin_and_signed_out_get_clear_errors()
    {
        await Run(null, null, null, "init", "--console", ConsoleUrl, "--only", "database");
        Write("database.rules", DatabaseRules);

        var member = await Run(KeycloakTokens.Mint(["Member"]), null, null, "rules", "deploy");
        Assert.Equal(1, member.Exit);
        Assert.Contains("Admin role", member.Err);

        var signedOut = await Run(null, null, null, "rules", "deploy");
        Assert.Equal(1, signedOut.Exit);
        Assert.Contains("winche login", signedOut.Err);
    }

    /// <summary>
    /// The whole browser sign-in: the CLI reads the console's Keycloak config, "opens" the authorization URL
    /// (this test plays the browser and Keycloak), receives the code on its loopback listener, exchanges it
    /// with PKCE, saves the tokens, refreshes an expired one, and deploys with it.
    /// </summary>
    [Fact]
    public async Task Login_with_pkce_loopback_then_refresh_and_deploy()
    {
        await Run(null, null, null, "init", "--console", ConsoleUrl, "--only", "database");
        Write("database.rules", DatabaseRules);

        var keycloak = new FakeKeycloak(App.Server.CreateHandler());
        var port = FreePort();
        string? challenge = null;
        using var browser = new HttpClient();
        void OpenBrowser(string url)
        {
            var q = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            Assert.Equal("winche-console", q["client_id"]);
            Assert.Equal($"http://127.0.0.1:{port}/callback", q["redirect_uri"]);
            Assert.Equal("S256", q["code_challenge_method"]);
            challenge = q["code_challenge"];
            _ = browser.GetAsync($"{q["redirect_uri"]}?code=the-code&state={q["state"]}");
        }

        var login = await Run(null, keycloak, OpenBrowser, "login", "--port", port.ToString());
        Assert.True(login.Exit == 0, login.Err);
        Assert.Contains("as user@test", login.Out);
        Assert.Equal(challenge, keycloak.ChallengeFor(keycloak.LastVerifier!));

        // The saved access token is expired: deploy refreshes it first.
        keycloak.ExpiresIn = 3600;
        var store = JsonDocument.Parse(File.ReadAllText(Path.Combine(_configDir, "credentials.json"))).RootElement;
        Assert.True(store.TryGetProperty(ConsoleUrl, out _));

        var deploy = await Run(null, keycloak, null, "rules", "deploy");
        Assert.True(deploy.Exit == 0, deploy.Err);
        Assert.Contains("deployed database.rules as v1", deploy.Out);
        Assert.Equal(1, keycloak.Refreshes);

        var logout = await Run(null, keycloak, null, "logout");
        Assert.Contains("Signed out", logout.Out);
        Assert.Contains("winche login", (await Run(null, keycloak, null, "rules", "deploy")).Err);
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    /// <summary>Answers Keycloak's discovery and token endpoints; everything else goes to the test server.</summary>
    private sealed class FakeKeycloak(HttpMessageHandler server) : DelegatingHandler(server)
    {
        public int ExpiresIn { get; set; } = 0;   // first token is already expired
        public int Refreshes { get; private set; }
        public string? LastVerifier { get; private set; }

        public string ChallengeFor(string verifier) =>
            Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host != "kc.test") return await base.SendAsync(request, ct);

            if (request.RequestUri.AbsolutePath.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
                return Json(new
                {
                    authorization_endpoint = "https://kc.test/realms/test/protocol/openid-connect/auth",
                    token_endpoint = "https://kc.test/realms/test/protocol/openid-connect/token",
                });

            var form = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct));
            switch (form["grant_type"])
            {
                case "authorization_code" when form["code"] == "the-code":
                    LastVerifier = form["code_verifier"];
                    break;
                case "refresh_token" when form["refresh_token"] == "refresh-1":
                    Refreshes++;
                    break;
                default:
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json"),
                    };
            }
            return Json(new
            {
                access_token = KeycloakTokens.Mint(["Admin"]),
                refresh_token = "refresh-1",
                expires_in = ExpiresIn,
            });
        }

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
    }
}
