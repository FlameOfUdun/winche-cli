using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Winche.Cli;
using Xunit;

namespace Winche.Cli.Tests;

/// <summary>
/// Runs the <c>winche</c> CLI in-process against a host app's console (the same rules API the console's editor uses),
/// with a console token got as <c>winche login</c> gets one, as <c>WINCHE_TOKEN</c>.
/// </summary>
[Collection("postgres")]
public sealed class CliTests(PostgresFixture fx) : IAsyncLifetime, IDisposable
{
    private const string ConsoleUrl = "http://localhost/projects/demo";
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

    private string? _stdin;

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
            In = new StringReader(_stdin ?? ""),
            Interactive = _stdin is not null,
        };
        var exit = await CliApp.RunAsync(args, env);
        return new(exit, stdout.ToString(), stderr.ToString());
    }

    private async Task<Result> Admin(params string[] args) => await Run(await App.AdminTokenAsync(), null, null, args);

    private void Write(string file, string text) => File.WriteAllText(Path.Combine(_dir, file), text);

    private const string DatabaseRules = """
        match users/{uid} {
          allow read, write: if request.auth.uid == uid;
        }
        """;

    [Fact]
    public async Task Indexes_are_pulled_diffed_and_deployed_as_a_file()
    {
        Write("winche.json", $$"""{ "server": "http://localhost", "project": "demo", "rules": {} }""");
        var pulled = await Admin("indexes", "pull");
        Assert.True(pulled.Exit == 0, pulled.Out + pulled.Err);
        Write("database.indexes", """
            // Sessions by user, newest first; gone once they expire.
            collection sessions {
              index (userId, startedAt desc)
              expire when expiresAt
            }
            """);
        var diff = await Admin("indexes", "diff");
        Assert.Contains("+ collection sessions {", diff.Out);
        var deploy = await Admin("indexes", "deploy", "-m", "Sessions by user");
        Assert.True(deploy.Exit == 0, deploy.Out + deploy.Err);
        Assert.Contains("deployed as version", deploy.Out);
        Assert.Contains("no changes", (await Admin("indexes", "diff")).Out);   // comments aside, the same
        Assert.Contains("no changes", (await Admin("indexes", "deploy")).Out);

        Write("database.indexes", "collection sessions {\n  index (userId startedAt)\n}\n");
        var bad = await Admin("indexes", "deploy");
        Assert.NotEqual(0, bad.Exit);
        Assert.Contains("database.indexes:2:", bad.Err);

        File.Delete(Path.Combine(_dir, "database.indexes"));
        Assert.Equal(0, (await Admin("indexes", "pull")).Exit);
        Assert.Contains("expire when expiresAt", File.ReadAllText(Path.Combine(_dir, "database.indexes")));
    }

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
        client.DefaultRequestHeaders.Authorization = new("Bearer", await App.AdminTokenAsync());
        var versions = JsonDocument.Parse(await client.GetStringAsync("/console/v1/rules/database/versions")).RootElement;
        Assert.Equal("first", versions[0].GetProperty("note").GetString());
        var live = JsonDocument.Parse(await client.GetStringAsync("/console/v1/rules/database/live")).RootElement;
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

        var member = await Run(await ConsoleTokens.ForAsync(App, "member@example.com", "Member"), null, null, "rules", "deploy");
        Assert.Equal(1, member.Exit);
        Assert.Contains("Admin role", member.Err);

        var signedOut = await Run(null, null, null, "rules", "deploy");
        Assert.Equal(1, signedOut.Exit);
        Assert.Contains("winche login", signedOut.Err);
    }

    /// <summary>
    /// The whole browser sign-in: the CLI "opens" the console's sign-in page for it (this test plays the browser, signed in
    /// as the admin, and approves), receives the code on its loopback listener, trades it with its PKCE verifier for a
    /// session of the account, saves it, deploys with it, adds and switches to a second account, and signs out.
    /// </summary>
    [Fact]
    public async Task Login_through_the_console_then_deploy_switch_accounts_and_logout()
    {
        await Run(null, null, null, "init", "--console", ConsoleUrl, "--only", "database");
        Write("database.rules", DatabaseRules);
        var admin = await ConsoleTokens.SignedInAsync(App, ConsoleTokens.AdminEmail, setup: true);
        (await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(admin, "/console/v1/accounts",
            new { email = "second@example.com", role = "Member", password = ConsoleTokens.Password })).EnsureSuccessStatusCode();
        var member = await ConsoleTokens.SignedInAsync(App, "second@example.com", setup: false);
        var browser = admin;   // who's signed in to the browser that approves
        var port = FreePort();
        using var loopback = new HttpClient();
        void OpenBrowser(string url)
        {
            var q = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            Assert.Equal(port.ToString(), q["port"]);
            Assert.StartsWith("http://localhost/console/cli-login", url);   // the console's page, whichever project
            var by = browser;
            _ = Task.Run(async () =>
            {
                var approved = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(by, "/console/v1/cli/approve",
                    new { challenge = q["challenge"], port = int.Parse(q["port"]!), state = q["state"], name = q["name"] });
                var redirect = JsonDocument.Parse(await approved.Content.ReadAsStringAsync()).RootElement.GetProperty("redirect").GetString()!;
                await loopback.GetAsync(redirect);
            });
        }

        var login = await Run(null, null, OpenBrowser, "login", "--port", port.ToString());
        Assert.True(login.Exit == 0, login.Out + login.Err);
        Assert.Contains("as admin@example.com", login.Out);
        var store = JsonDocument.Parse(File.ReadAllText(Path.Combine(_configDir, "credentials.json"))).RootElement;
        Assert.Equal("admin@example.com", store.GetProperty("servers").GetProperty("http://localhost").GetProperty("active").GetString());
        Assert.Contains("Already signed in", (await Run(null, null, null, "login")).Out);   // no browser: says so

        var deploy = await Run(null, null, null, "rules", "deploy");
        Assert.True(deploy.Exit == 0, deploy.Err);
        Assert.Contains("deployed database.rules as v1", deploy.Out);

        // A second account beside it; the first stays in use until login:use.
        browser = member;
        var added = await Run(null, null, OpenBrowser, "login:add", "--port", port.ToString());
        Assert.True(added.Exit == 0, added.Out + added.Err);
        Assert.Contains("still using admin@example.com", added.Out);
        var list = await Run(null, null, null, "login:list");
        Assert.Contains("* admin@example.com", list.Out);
        Assert.Contains("  second@example.com", list.Out);
        Assert.Contains("Using second@example.com", (await Run(null, null, null, "login:use", "second@example.com")).Out);
        Assert.NotEqual(0, (await Run(null, null, null, "login:use", "nobody@example.com")).Exit);

        // logout <email> ends that one's session; the other takes over. logout ends the rest.
        Assert.Contains("Signed second@example.com out", (await Run(null, null, null, "logout", "second@example.com")).Out);
        Assert.Contains("* admin@example.com", (await Run(null, null, null, "login:list")).Out);
        Assert.Contains("Signed admin@example.com out", (await Run(null, null, null, "logout")).Out);
        Assert.Contains("winche login", (await Run(null, null, null, "rules", "deploy")).Err);
    }

    [Fact]
    public async Task Deploy_runs_the_tests_first_and_stops_on_a_failure()
    {
        await Run(null, null, null, "init", "--console", ConsoleUrl, "--only", "database");
        Write("database.rules", DatabaseRules);
        Write("database.rules.test", """
            test "owner reads" {
              auth { uid: 'u1' }
              allow get users/u1
            }
            test "stranger reads" {
              auth { uid: 'u2' }
              allow get users/u1
            }
            """);

        var blocked = await Admin("rules", "deploy");
        Assert.Equal(1, blocked.Exit);
        Assert.Contains("✗ stranger reads", blocked.Out);
        Assert.Contains("database.rules.test:7: allow get users/u1 — got deny", blocked.Out);
        Assert.Contains("nothing was deployed", blocked.Err);
        Assert.DoesNotContain("deployed database.rules", blocked.Out);

        var skipped = await Admin("rules", "deploy", "--skip-tests");
        Assert.Contains("deployed database.rules as v1", skipped.Out);
    }

    [Fact]
    public async Task History_lists_versions_shows_one_and_rollback_reverts()
    {
        await Run(null, null, null, "init", "--console", ConsoleUrl, "--only", "database");
        Write("database.rules", DatabaseRules);
        await Admin("rules", "deploy", "-m", "first");
        Write("database.rules", DatabaseRules.Replace("read, write", "read"));
        await Admin("rules", "deploy", "-m", "read only");

        var history = await Admin("rules", "history");
        Assert.Equal(0, history.Exit);
        Assert.Matches(@"\* v2 .*read only", history.Out);
        Assert.Matches(@"  v1 .*first", history.Out);

        var show = await Admin("rules", "history", "1");
        Assert.Contains("database v1 against the active v2:", show.Out);
        Assert.Contains("+   allow read, write:", show.Out);

        // No prompt possible (not interactive) and no --yes: refuse.
        var refused = await Admin("rules", "rollback", "1");
        Assert.Equal(2, refused.Exit);
        Assert.Contains("--yes", refused.Err);

        // Interactive "n" keeps v2.
        _stdin = "n\n";
        Assert.Contains("Not rolled back.", (await Admin("rules", "rollback", "1")).Out);
        _stdin = null;

        var rolled = await Admin("rules", "rollback", "v1", "--yes");
        Assert.Equal(0, rolled.Exit);
        Assert.Contains("database: v1 is active again as v3", rolled.Out);
        Assert.Matches(@"\* v3 .*Reverted to v1", (await Admin("rules", "history")).Out);
        Write("database.rules", DatabaseRules);   // v3 has v1's rules
        Assert.Contains("database: up to date (v3)", (await Admin("rules", "deploy", "--skip-tests")).Out);

        Assert.Contains("has no version 9", (await Admin("rules", "rollback", "9", "--yes")).Err);
        Assert.Contains("v3 is already active", (await Admin("rules", "rollback", "3", "--yes")).Out);
    }

    [Fact]
    public async Task Rollback_with_both_subsystems_asks_which()
    {
        await Run(null, null, null, "init", "--console", ConsoleUrl);
        var r = await Admin("rules", "rollback", "1", "--yes");
        Assert.Equal(2, r.Exit);
        Assert.Contains("--only database", r.Err);
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

}
