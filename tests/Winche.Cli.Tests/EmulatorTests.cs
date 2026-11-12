using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Winche.Cli.Emulator;
using Xunit;

namespace Winche.Cli.Tests;

/// <summary><c>winche emulators</c>: the database, storage and console in-process, on the test Postgres.</summary>
[Collection("postgres")]
public sealed class EmulatorTests(PostgresFixture fx) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("winche-emulator-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    private Task<EmulatorHost> Start(TextWriter log, string? databaseRules = null, string? storageRules = null) =>
        EmulatorHost.StartAsync(new EmulatorOptions
        {
            Port = 0,
            ConnectionString = fx.ConnectionString,
            DataDirectory = Path.Combine(_dir, "data"),
            DatabaseRules = databaseRules,
            StorageRules = storageRules,
        }, log, default);

    private static HttpClient As(EmulatorHost host, string? uid, params (string, object?)[] claims)
    {
        var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };
        if (uid is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                TestTokens.Mint(uid, claims.ToDictionary(c => c.Item1, c => c.Item2)));
        return client;
    }

    private static string Doc(string path) => "/documents/" + Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
    private static string FilePath(string path) => "/files/" + Convert.ToBase64String(Encoding.UTF8.GetBytes(path)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Task<HttpResponseMessage> Put(HttpClient c, string path, string name) =>
        c.PutAsync(Doc(path), JsonContent.Create(new { fields = new { name = new { stringValue = name } } }));

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 50; i++)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail("condition not met within 5 s");
    }

    [Fact]
    public async Task Rules_from_the_file_guard_requests_and_reload_on_save()
    {
        var rules = File("database.rules", "match users/{uid} {\n  allow read, write: if request.auth.uid == uid;\n}\n");
        var log = new StringWriter();
        await using var host = await Start(log, databaseRules: rules);
        using var u1 = As(host, "u1");
        using var u2 = As(host, "u2");
        using var anon = As(host, null);

        Assert.Equal(HttpStatusCode.OK, (await Put(u1, "users/u1", "Ann")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await u2.GetAsync(Doc("users/u1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anon.GetAsync(Doc("users/u1"))).StatusCode);
        var doc = await u1.GetFromJsonAsync<JsonObject>(Doc("users/u1"));
        Assert.Equal("Ann", doc!["fields"]!["name"]!["stringValue"]!.GetValue<string>());

        // Saving the file applies it straight away…
        File("database.rules", "match users/{uid} {\n  allow read: if request.auth != null;\n  allow write: if request.auth.uid == uid;\n}\n");
        await Eventually(async () => (await u2.GetAsync(Doc("users/u1"))).StatusCode == HttpStatusCode.OK);
        Assert.Contains("↻ database.rules reloaded", log.ToString());

        // …and a file with an error leaves the previous rules in force.
        File("database.rules", "match users/{uid} {\n  allow read: if request.auth ==;\n}\n");
        await Eventually(() => Task.FromResult(log.ToString().Contains("✗ database.rules:2:")));
        Assert.Equal(HttpStatusCode.OK, (await u2.GetAsync(Doc("users/u1"))).StatusCode);
        Assert.Contains("the previous rules stay in force", log.ToString());

        // Requests are logged with who made them and what happened.
        Assert.Contains("GET    /documents/users/u1 as u2 → 403  ← denied by the rules", log.ToString());
        Assert.Contains("PUT    /documents/users/u1 as u1 → 200", log.ToString());
    }

    [Fact]
    public async Task Files_upload_and_download_through_the_local_store()
    {
        var rules = File("storage.rules", "match uploads/{uid}/{file=**} {\n  allow read, write: if request.auth.uid == uid;\n}\n");
        await using var host = await Start(TextWriter.Null, storageRules: rules);
        using var u1 = As(host, "u1");
        using var u2 = As(host, "u2");
        var path = FilePath("uploads/u1/notes/a.txt");

        var created = await u1.PutAsJsonAsync(path, new { mimeType = "text/plain", sizeBytes = 5 });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await u2.PutAsJsonAsync(FilePath("uploads/u1/x.txt"), new { mimeType = "text/plain", sizeBytes = 1 })).StatusCode);

        var upload = await (await u1.PostAsync(path + ":upload", null)).Content.ReadFromJsonAsync<JsonObject>();
        var uploadUrl = upload!["url"]!.GetValue<string>();
        Assert.StartsWith(host.BaseUrl + "/_emulator/objects/uploads/u1/notes/a.txt", uploadUrl);
        using (var raw = new HttpClient())
            Assert.Equal(HttpStatusCode.OK, (await raw.PutAsync(uploadUrl, new StringContent("hello"))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await u1.PostAsync(path + ":confirm", null)).StatusCode);
        var download = await (await u1.PostAsync(path + ":download", null)).Content.ReadFromJsonAsync<JsonObject>();
        using (var raw = new HttpClient())
            Assert.Equal("hello", await raw.GetStringAsync(download!["url"]!.GetValue<string>()));
        Assert.True(System.IO.File.Exists(Path.Combine(_dir, "data", "storage", "objects", "uploads", "u1", "notes", "a.txt")));
    }

    [Fact]
    public async Task The_console_is_served_and_its_admin_can_sign_in()
    {
        await using var host = await Start(TextWriter.Null);
        using var client = new HttpClient(new HttpClientHandler { UseCookies = true }) { BaseAddress = new Uri(host.BaseUrl) };
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/_console/")).StatusCode);
        var login = await client.PostAsJsonAsync("/_console/api/auth/login",
            new { email = EmulatorHost.ConsoleEmail, password = EmulatorHost.ConsolePassword });
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Exec_runs_a_command_against_the_emulators_and_returns_its_exit_code()
    {
        File("winche.json", """{ "rules": { "database": "database.rules" } }""");
        File("database.rules", "match users/{uid} { allow read; }");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await CliApp.RunAsync(
            ["emulators", "exec", "--port", "0", "--pg", fx.ConnectionString, "--data", Path.Combine(_dir, "data"),
             "curl -sf \"$WINCHE_DATABASE_URL/ping\" > /dev/null && test \"$WINCHE_STORAGE_URL\" = \"$WINCHE_EMULATOR/files\" && exit 7"],
            new CliEnvironment
            {
                Out = stdout, Error = stderr, WorkingDirectory = _dir, ConfigDirectory = _dir,
                GetVariable = _ => null, Http = new HttpClientHandler(), OpenBrowser = _ => { },
            });
        Assert.True(exit == 7, stdout + "\n" + stderr);
        Assert.Contains("exited with 7; stopping the emulators", stdout.ToString());
    }

    [Fact]
    public void Tokens_carry_uid_and_claims()
    {
        var claims = TestTokens.Decode(TestTokens.Mint("u1", new Dictionary<string, object?> { ["role"] = "admin", ["level"] = 3L }))!;
        Assert.Equal("u1", claims["uid"]);
        Assert.Equal("u1", claims["sub"]);
        Assert.Equal("admin", claims["role"]);
        Assert.Equal("Int64", claims["level"]!.GetType().Name);
        Assert.Equal(3L, (long)claims["level"]!);
        Assert.Equal("k", TestTokens.Decode("x." + Convert.ToBase64String("{\"sub\":\"k\"}"u8.ToArray()).TrimEnd('=') + ".")!["uid"]);
        Assert.Null(TestTokens.Decode("garbage"));
    }
}
