using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace Winche.Cli.Tests;

/// <summary>A server that isn't set up yet, in databases of its own: what <c>winche setup</c> starts from.</summary>
public sealed class FreshServerFactory(PostgresFixture fx, string name) : WebApplicationFactory<global::Program>
{
    public static async Task<FreshServerFactory> CreateAsync(PostgresFixture fx)
    {
        var name = "fresh_" + Guid.NewGuid().ToString("N")[..8];
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        foreach (var db in new[] { name + "_app", name + "_console" })
            await using (var cmd = new NpgsqlCommand($"CREATE DATABASE {db}", conn)) await cmd.ExecuteNonQueryAsync();
        return new FreshServerFactory(fx, name);
    }

    private string Conn(string db) => new NpgsqlConnectionStringBuilder(fx.ConnectionString) { Database = db }.ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Winche:ConnectionString", Conn(name + "_app"));
        builder.UseSetting("Console:ConnectionString", Conn(name + "_console"));
        builder.UseSetting("TestHost:ProjectSetup", "true");
    }
}

/// <summary>Using a Winche server from the terminal only: setup, sign-in without a browser, products, CI tokens, settings files.</summary>
[Collection("postgres")]
public sealed class ServerCommandsTests(PostgresFixture fx) : IAsyncLifetime
{
    private const string Url = "http://localhost";
    private const string Password = "Passw0rd!long";
    private FreshServerFactory _app = null!;
    private readonly string _dir = Directory.CreateTempSubdirectory("winche-cli-server-").FullName;
    private readonly string _configDir = Directory.CreateTempSubdirectory("winche-cli-config-").FullName;
    private readonly Dictionary<string, string> _vars = new();

    public async Task InitializeAsync() => _app = await FreshServerFactory.CreateAsync(fx);

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        foreach (var d in new[] { _dir, _configDir }) if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
    }

    private sealed record Result(int Exit, string Out, string Err);

    private async Task<Result> Run(string? stdin, params string[] args) => await Run(stdin, null, args);

    private async Task<Result> Run(string? stdin, TextWriter? output, params string[] args)
    {
        var stdout = output ?? new StringWriter();
        var stderr = new StringWriter();
        var lines = new Queue<string>((stdin ?? "").Split('\n'));
        var env = new CliEnvironment
        {
            Out = stdout,
            Error = stderr,
            WorkingDirectory = _dir,
            ConfigDirectory = _configDir,
            GetVariable = name => _vars.GetValueOrDefault(name),
            Http = _app.Server.CreateHandler(),
            OpenBrowser = _ => throw new InvalidOperationException("no browser in tests"),
            In = new StringReader(stdin ?? ""),
            ReadSecret = () => _vars.GetValueOrDefault("TEST_SECRET"),
            Interactive = stdin is not null,
        };
        var exit = await CliApp.RunAsync(args, env);
        return new(exit, stdout.ToString()!, stderr.ToString());
    }

    private async Task SetupAsync()
    {
        _vars["WINCHE_ADMIN_PASSWORD"] = Password;
        var setup = await Run(null, "setup", "--url", "http://localhost", "--code", "anything", "--email", "ann@example.com",
            "--first-name", "Ann", "--last-name", "Admin");
        Assert.True(setup.Exit == 0, setup.Out + setup.Err);
        await MakeProjectAsync("clinic", "Clinic");
        File.WriteAllText(Path.Combine(_dir, "winche.json"), $$"""{ "server": "{{Url}}", "project": "demo", "rules": {} }""");
    }

    /// <summary>
    /// The test host is one app's console (with project setup): its project made as a server's Projects page makes one
    /// (setup itself makes only the admin).
    /// </summary>
    private async Task MakeProjectAsync(string id, string name)
    {
        var console = _app.CreateClient();
        Assert.True((await console.PostAsJsonAsync("/console/v1/session/login", new { email = "ann@example.com", password = Password })).IsSuccessStatusCode);
        var made = await console.PostAsJsonAsync("/console/v1/project", new { name, id, products = Array.Empty<string>() });
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Setup_makes_the_admin_and_signs_in_here()
    {
        await SetupAsync();
        var login = await Run(null, "login");
        Assert.Contains("Already signed in to http://localhost as ann@example.com", login.Out);

        // The project exists, its products off; starting one follows its setup to the end.
        var products = await Run(null, "products");
        Assert.Contains("database         off", products.Out);
        var started = await Run(null, "products:start", "database");
        Assert.True(started.Exit == 0, started.Out + started.Err);
        Assert.Contains("database is started.", started.Out);
        Assert.Contains("database         started", (await Run(null, "products")).Out);
        Assert.Equal(2, (await Run(null, "products:start", "nope")).Exit);

        // Run again, it carries on with nothing left to do: projects are made with projects:create.
        var again = await Run(null, "setup", "--url", "http://localhost");
        Assert.True(again.Exit == 0, again.Err);
        Assert.Contains("winche projects:create", again.Out);

        // Set up already, a new machine is told to sign in instead.
        Directory.Delete(_configDir, recursive: true);
        var other = await Run(null, "setup", "--url", "http://localhost", "--code", "x", "--email", "bob@example.com");
        Assert.NotEqual(0, other.Exit);
        Assert.Contains("already set up", other.Err);
    }

    [Fact]
    public async Task Setup_asks_for_what_it_isnt_given()
    {
        _vars["TEST_SECRET"] = Password;
        var setup = await Run("the-code\nann@example.com\nAnn\n\n", "setup", "--url", "http://localhost");
        Assert.True(setup.Exit == 0, setup.Out + setup.Err);
        Assert.Contains("Made the admin account ann@example.com", setup.Out);
        // Nobody to ask: it says which option is missing.
        Directory.Delete(_configDir, recursive: true);
        var blind = await Run(null, "setup", "--url", "http://localhost");
        Assert.Equal(2, blind.Exit);
        Assert.Contains("Setup code", blind.Err);
    }

    [Fact]
    public async Task Login_without_a_browser_shows_a_code_to_allow_anywhere()
    {
        await SetupAsync();
        using var browser = _app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await browser.PostAsJsonAsync("/console/v1/session/login", new { email = "ann@example.com", password = Password })).EnsureSuccessStatusCode();

        var text = new StringWriter();
        var output = TextWriter.Synchronized(text);
        var login = Run(null, output, "login:add", "--no-browser");
        string? code = null;
        for (var i = 0; i < 100 && code is null; i++)
        {
            await Task.Delay(50);
            code = System.Text.RegularExpressions.Regex.Match(text.ToString(), @"Your code: ([A-Z]{4}-[A-Z]{4})") is { Success: true } m ? m.Groups[1].Value : null;
        }
        Assert.True(code is not null, text + (login.IsCompleted ? (await login).Err : ""));
        Assert.Contains($"cli-login?user_code={code}", text.ToString());

        // The page shows which CLI; a wrong code is refused; the right one is allowed.
        var device = JsonNode.Parse(await browser.GetStringAsync($"/console/v1/cli/device/{code.ToLowerInvariant().Replace("-", "")}"))!;
        Assert.StartsWith("winche CLI on ", device["name"]!.GetValue<string>());
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await browser.PostAsJsonAsync("/console/v1/cli/approve", new { userCode = "BBBB-BBBB", port = 0 })).StatusCode);
        (await browser.PostAsJsonAsync("/console/v1/cli/approve", new { userCode = code, port = 0 })).EnsureSuccessStatusCode();

        var result = await login;
        Assert.True(result.Exit == 0, result.Out + result.Err);
        Assert.Contains("Signed in to http://localhost as ann@example.com", text.ToString());
        // Allowed once: the code's used up.
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await browser.GetAsync($"/console/v1/cli/device/{code}")).StatusCode);
    }

    [Fact]
    public async Task Two_step_sign_in_is_turned_on_from_the_terminal()
    {
        await SetupAsync();
        // Three wrong codes: not turned on. The test reads the key the CLI prints and answers as an authenticator would.
        var wrong = await Run("000000\n000001\n000002\n", "2fa", "enable");
        Assert.NotEqual(0, wrong.Exit);
        Assert.Contains("didn't work", wrong.Out);
        var key = System.Text.RegularExpressions.Regex.Match(wrong.Out, @"Key: ([a-z0-9 ]+)").Groups[1].Value.Replace(" ", "");
        Assert.True(key.Length > 0, wrong.Out + wrong.Err);
        var right = await Run(null, "2fa", "enable", "--code", Totp(key));
        Assert.True(right.Exit == 0, right.Out + right.Err);
        // Turning it on ends the account's other sessions, not this one: the CLI carries on (after the console's
        // 10-second session check comes round).
        await Task.Delay(TimeSpan.FromSeconds(11));
        var after = await Run(null, "tokens:list");
        Assert.True(after.Exit == 0, after.Err);
        Assert.Contains("Two-step sign-in is on", right.Out);
        Assert.Matches(@"\n  \S+", right.Out[right.Out.IndexOf("safe", StringComparison.Ordinal)..]);   // recovery codes
    }

    [Fact]
    public async Task Ci_tokens_are_made_listed_and_revoked_from_the_terminal()
    {
        await SetupAsync();
        Assert.Contains("No CI tokens", (await Run(null, "tokens:list")).Out);
        var made = await Run(null, "tokens:create", "gitlab-main", "--scopes", "rules,indexes", "--days", "30", "--quiet");
        Assert.True(made.Exit == 0, made.Err);
        var secret = made.Out.Trim();
        Assert.StartsWith("wct_", secret);
        Assert.Contains("gitlab-main", (await Run(null, "tokens:list")).Out);
        Assert.NotEqual(0, (await Run(null, "tokens:create", "x", "--scopes", "everything")).Exit);

        // The token works, and can't make tokens itself.
        _vars["WINCHE_TOKEN"] = secret;
        Assert.Equal(0, (await Run(null, "rules", "history", "--only", "database")).Exit);
        Assert.NotEqual(0, (await Run(null, "tokens:create", "y", "--scopes", "rules")).Exit);
        _vars.Remove("WINCHE_TOKEN");

        Assert.Contains("Revoked the CI token gitlab-main", (await Run(null, "tokens:revoke", "gitlab-main")).Out);
        Assert.Contains("No CI tokens", (await Run(null, "tokens:list")).Out);
        Assert.Contains("No CI token gitlab-main", (await Run(null, "tokens:revoke", "gitlab-main")).Err);
    }

    [Fact]
    public async Task Settings_are_pulled_diffed_and_deployed_as_files_leaving_indexes_alone()
    {
        await SetupAsync();
        var start = await Run(null, "products:start", "database", "storage");
        Assert.True(start.Exit == 0, start.Out + start.Err);
        File.WriteAllText(Path.Combine(_dir, "database.indexes"), "collection users {\n  index (age, name desc)\n}\n");
        var ix = await Run(null, "indexes", "deploy", "-m", "an index");
        Assert.True(ix.Exit == 0, ix.Out + ix.Err);

        var pull = await Run(null, "settings", "pull");
        Assert.True(pull.Exit == 0, pull.Err);
        Assert.Contains("wrote settings/database.json", pull.Out);
        Assert.Contains("wrote settings/storage.json", pull.Out);
        var path = Path.Combine(_dir, "settings", "database.json");
        var file = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.False(file.ContainsKey("indexes"));   // they're database.indexes
        Assert.Contains("database: unchanged", (await Run(null, "settings", "diff")).Out);

        // Change one value: diff shows it, deploy saves it with the note, and the index is still there.
        var before = File.ReadAllText(path);
        const string key = "slowQueryMs";
        file[key] = file[key]!.GetValue<int>() + 250;
        File.WriteAllText(path, file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var diff = await Run(null, "settings", "diff", "--only", "database");
        Assert.Equal(1, diff.Exit);
        Assert.Contains($"\"{key}\"", diff.Out);
        Assert.Contains("deployed settings/database.json as v", (await Run(null, "settings", "deploy", "-m", "flip it")).Out);
        Assert.Contains("database: unchanged", (await Run(null, "settings", "diff")).Out);
        Assert.Contains("index (age, name desc)", (await Run(null, "indexes", "pull", "--stdout")).Out);
        Assert.Contains("Nothing to deploy.", (await Run(null, "settings", "deploy")).Out);

        // A bad file is refused with the server's reasons, and indexes can't sneak in.
        file["indexes"] = new JsonArray();
        File.WriteAllText(path, file.ToJsonString());
        Assert.Contains("kept in database.indexes", (await Run(null, "settings", "deploy")).Err);
        File.WriteAllText(path, before.Replace("{", "{ \"nope\": ", StringComparison.Ordinal));
        Assert.Contains("isn't valid JSON", (await Run(null, "settings", "deploy")).Err);
    }

    [Fact]
    public async Task More_projects_are_made_from_the_terminal()
    {
        await SetupAsync();
        // This host runs one project: the server says so.
        var made = await Run(null, "projects:create", "wards", "--name", "Wards");
        Assert.NotEqual(0, made.Exit);
        Assert.Contains("one project", made.Err);
    }

    [Fact]
    public async Task Apps_are_registered_and_listed_from_the_terminal()
    {
        await SetupAsync();
        Assert.Contains("No apps registered", (await Run(null, "apps")).Out);
        var made = await Run(null, "apps:create", "Clinic iOS", "--id", "clinic-ios", "--apple-bundle-id", "com.example.clinic", "--web-origin", "https://a.example, https://b.example");
        Assert.True(made.Exit == 0, made.Err);
        Assert.Contains("winche configure --url http://localhost --project demo --app clinic-ios", made.Out);
        var list = await Run(null, "apps");
        Assert.Contains("clinic-ios", list.Out);
        Assert.Contains("com.example.clinic, https://a.example, https://b.example", list.Out);
        Assert.Contains("isn't an SDK", (await Run(null, "apps:create", "X", "--sdk", "swift")).Err);
        Assert.Contains("There's already an app clinic-ios", (await Run(null, "apps:create", "Again", "--id", "clinic-ios")).Err);
    }

    [Fact]
    public async Task Members_are_invited_or_given_a_role_in_the_first_project()
    {
        await SetupAsync();
        var invited = await Run(null, "members:add", "dave@example.com", "--role", "viewer");
        Assert.True(invited.Exit == 0, invited.Err);
        Assert.Contains("Invited dave@example.com as Viewer", invited.Out);
        Assert.Contains("/invite?token=", invited.Out);   // no email here: the link to pass on
        Assert.Contains("A pending invite already exists", (await Run(null, "members:add", "dave@example.com")).Err);
        Assert.Equal(2, (await Run(null, "members:add", "x@example.com", "--role", "Owner")).Exit);

        // Someone with an account (and no role) gets one.
        using var admin = _app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await admin.PostAsJsonAsync("/console/v1/session/login", new { email = "ann@example.com", password = Password })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/console/v1/accounts", new { email = "erin@example.com", role = "Viewer", password = Password })).EnsureSuccessStatusCode();
        Assert.Contains("erin@example.com is now Member here", (await Run(null, "members:add", "erin@example.com")).Out);
        var members = await Run(null, "members");
        Assert.Contains("ann@example.com", members.Out);
        Assert.Matches(@"erin@example\.com\s+Member", members.Out);
        Assert.Matches(@"dave@example\.com\s+Viewer \(invited\)", members.Out);
    }

    [Fact]
    public async Task Access_is_taken_back_from_the_terminal()
    {
        await SetupAsync();
        // An invite revoked; a role taken away (the account stays); an app removed, after asking.
        await Run(null, "members:add", "dave@example.com", "--role", "Viewer");
        Assert.Contains("dave@example.com", (await Run(null, "invites")).Out);
        Assert.Contains("Revoked dave@example.com's invite", (await Run(null, "invites:revoke", "dave@example.com")).Out);
        Assert.Contains("No pending invites", (await Run(null, "invites")).Out);

        using var admin = _app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await admin.PostAsJsonAsync("/console/v1/session/login", new { email = "ann@example.com", password = Password })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/console/v1/accounts", new { email = "erin@example.com", role = "Member", password = Password })).EnsureSuccessStatusCode();
        Assert.Contains("erin@example.com no longer has a role here", (await Run(null, "members:remove", "erin@example.com")).Out);
        Assert.DoesNotContain("erin@example.com", (await Run(null, "members")).Out);
        Assert.Contains("has no access here", (await Run(null, "members:remove", "erin@example.com")).Err);

        await Run(null, "apps:create", "Clinic", "--id", "clinic-app");
        var unasked = await Run(null, "apps:delete", "clinic-app");
        Assert.Equal(2, unasked.Exit);
        Assert.Contains("Pass --yes", unasked.Err);
        Assert.Equal(1, (await Run("n\n", "apps:delete", "clinic-app")).Exit);   // answered no
        Assert.Contains("Removed the app clinic-app", (await Run(null, "apps:delete", "clinic-app", "--yes")).Out);
        Assert.Contains("No app clinic-app", (await Run(null, "apps:delete", "clinic-app", "--yes")).Err);
    }

    [Fact]
    public async Task A_signed_in_cli_approves_another_waiting_with_a_code()
    {
        await SetupAsync();
        var otherConfig = Directory.CreateTempSubdirectory("winche-cli-other-").FullName;
        var text = new StringWriter();
        var output = TextWriter.Synchronized(text);
        var env = new CliEnvironment
        {
            Out = output, Error = new StringWriter(), WorkingDirectory = _dir, ConfigDirectory = otherConfig,
            GetVariable = _ => null, Http = _app.Server.CreateHandler(), OpenBrowser = _ => { },
        };
        var waiting = CliApp.RunAsync(["login", "--url", "http://localhost", "--no-browser"], env);
        string? code = null;
        for (var i = 0; i < 100 && code is null; i++)
        {
            await Task.Delay(50);
            code = System.Text.RegularExpressions.Regex.Match(text.ToString(), @"Your code: ([A-Z]{4}-[A-Z]{4})") is { Success: true } m ? m.Groups[1].Value : null;
        }
        Assert.NotNull(code);
        Assert.Contains("isn't right", (await Run(null, "login:approve", "BBBB-BBBB", "--yes")).Err);
        var approved = await Run(null, "login:approve", code!, "--yes");
        Assert.True(approved.Exit == 0, approved.Err);
        Assert.Equal(0, await waiting);
        Assert.Contains("Signed in to http://localhost as ann@example.com", text.ToString());

        // A CI token can't approve anyone.
        var ci = (await Run(null, "tokens:create", "ci", "--scopes", "rules", "--quiet")).Out.Trim();
        var device = JsonNode.Parse(await (await _app.CreateClient().PostAsJsonAsync("/console/v1/cli/device",
            new { challenge = new string('a', 43), name = "box" })).Content.ReadAsStringAsync())!;
        _vars["WINCHE_TOKEN"] = ci;
        Assert.NotEqual(0, (await Run(null, "login:approve", device["userCode"]!.GetValue<string>(), "--yes")).Exit);
        _vars.Remove("WINCHE_TOKEN");
        Assert.Contains("pending", await (await _app.CreateClient().PostAsJsonAsync("/console/v1/cli/device/poll",
            new { deviceCode = device["deviceCode"]!.GetValue<string>() })).Content.ReadAsStringAsync());
        Directory.Delete(otherConfig, recursive: true);
    }

    [Fact]
    public async Task Tokens_quiet_prints_only_the_token_even_with_a_named_project()
    {
        await SetupAsync();
        File.WriteAllText(Path.Combine(_dir, "winche.json"), $$"""{ "server": "{{Url}}", "project": "demo", "projects": { "main": "{{Url}}/projects/demo" }, "rules": {} }""");
        var made = await Run(null, "tokens:create", "ci", "--scopes", "rules", "--quiet");
        Assert.True(made.Exit == 0, made.Err);
        Assert.Matches(@"^wct_\S+\n$", made.Out.Replace("\r", ""));
        // A pasted token with a line break in it is refused plainly.
        _vars["WINCHE_TOKEN"] = made.Out.Trim() + "\nProject: main";
        var bad = await Run(null, "rules", "history");
        Assert.Equal(2, bad.Exit);
        Assert.Contains("more than one line", bad.Err);
    }

    [Fact]
    public async Task Configure_reads_the_named_projects_own_setup()
    {
        var seen = new List<string>();
        var handler = new Recording(seen, """{ "projectId": "wards", "name": "Wards", "baseUrl": "http://api.example.com/projects/wards", "services": [], "providers": [], "app": null }""");
        var env = new CliEnvironment
        {
            Out = new StringWriter(), Error = new StringWriter(), WorkingDirectory = _dir, ConfigDirectory = _configDir,
            GetVariable = _ => null, Http = handler, OpenBrowser = _ => { },
        };
        File.WriteAllText(Path.Combine(_dir, "pubspec.yaml"), "name: demo\n");
        Assert.Equal(0, await CliApp.RunAsync(["configure", "--url", "http://api.example.com", "--project", "wards"], env));
        Assert.Contains("/projects/wards/.well-known/winche", seen);
    }

    private sealed class Recording(List<string> seen, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            seen.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    /// <summary>The 6-digit code an authenticator shows now for <paramref name="base32"/> (RFC 6238).</summary>
    private static string Totp(string base32)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(base32.ToUpperInvariant().TrimEnd('=').Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
        var key = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
        var counter = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0f;
        var value = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("D6");
    }
}
