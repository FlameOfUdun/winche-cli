using System.Net;
using System.Net.Http.Headers;
using Winche.Cli.Emulator;
using Xunit;

namespace Winche.Cli.Tests;

/// <summary>winche assets: init, deploy (only new contents sent), diff, releases, channels and rollback; the emulator serving the folder.</summary>
[Collection("postgres")]
public sealed class AssetsCliTests(PostgresFixture fx) : IDisposable
{
    private const string ConsoleUrl = "http://localhost/_console";
    private readonly string _dir = Directory.CreateTempSubdirectory("winche-cli-assets-").FullName;
    private readonly string _configDir = Directory.CreateTempSubdirectory("winche-cli-config-").FullName;
    private readonly string _channel = $"c{Guid.NewGuid():N}"[..12];   // the database is shared between tests
    private ConsoleAppFactory? _app;
    private string? _stdin;

    public void Dispose()
    {
        _app?.Dispose();
        Directory.Delete(_dir, recursive: true);
        Directory.Delete(_configDir, recursive: true);
    }

    private ConsoleAppFactory App => _app ??= new ConsoleAppFactory(fx);

    private sealed record Result(int Exit, string Out, string Err)
    {
        public string All => Out + Err;
    }

    private async Task<Result> Admin(params string[] args)
    {
        var token = await App.AdminTokenAsync();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await CliApp.RunAsync(args, new CliEnvironment
        {
            Out = stdout, Error = stderr, WorkingDirectory = _dir, ConfigDirectory = _configDir,
            GetVariable = name => name == "WINCHE_TOKEN" ? token : null,
            Http = App.Server.CreateHandler(),
            OpenBrowser = _ => throw new InvalidOperationException("no browser in tests"),
            In = new StringReader(_stdin ?? ""), Interactive = _stdin is not null,
        });
        return new(exit, stdout.ToString(), stderr.ToString());
    }

    private void Write(string file, string text)
    {
        var path = Path.Combine(_dir, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public async Task Init_deploy_diff_and_rolling_back_a_channel()
    {
        Write("winche.json", $$"""{ "console": "{{ConsoleUrl}}", "rules": {} }""");
        var init = await Admin("assets", "init");
        Assert.True(init.Exit == 0, init.All);
        Assert.True(File.Exists(Path.Combine(_dir, "assets", "translations", "en.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "assets.rules")));
        var json = File.ReadAllText(Path.Combine(_dir, "winche.json"));
        Assert.Contains("\"assets\": \"assets.rules\"", json);
        Assert.Contains("\"folder\": \"assets\"", json);

        // The rules file is one of the rules files now: validated, tested and deployed with the others.
        Write("assets.rules", "match translations/{file=**} { allow read; }\n");
        Write("assets.rules.test", "test \"translations are public\" {\n  allow get translations/en.json\n  deny get secret/x.txt\n}\n");
        var rules = await Admin("rules", "deploy", "--only", "assets", "-m", "Public translations");
        Assert.True(rules.Exit == 0, rules.All);

        Write("assets/pages/welcome.html", "<h1>Welcome</h1>");
        var first = await Admin("assets", "deploy", "--channel", _channel, "-m", "First");
        Assert.True(first.Exit == 0, first.All);
        Assert.Contains($"{_channel} is new: 2 files", first.Out);
        Assert.Matches($"Published release #\\d+ \\(2 files, .*\\); {_channel} points at it", first.Out);
        var r1 = int.Parse(System.Text.RegularExpressions.Regex.Match(first.Out, "release #(\\d+)").Groups[1].Value);

        var again = await Admin("assets", "deploy", "--channel", _channel);
        Assert.Contains("Nothing to publish", again.Out);

        Write("assets/translations/en.json", "{ \"hello\": \"Hi\" }");
        Write("assets/translations/de.json", "{ \"hello\": \"Hallo\" }");
        File.Delete(Path.Combine(_dir, "assets", "pages", "welcome.html"));
        var diff = await Admin("assets", "diff", "--channel", _channel);
        Assert.Contains("  + translations/de.json", diff.Out);
        Assert.Contains("  ~ translations/en.json", diff.Out);
        Assert.Contains("  - pages/welcome.html", diff.Out);
        var dry = await Admin("assets", "deploy", "--channel", _channel, "--dry-run");
        Assert.Contains("Dry run", dry.Out);

        var second = await Admin("assets", "deploy", "--channel", _channel, "-m", "German");
        Assert.True(second.Exit == 0, second.All);
        Assert.Contains("Sending 2 new files", second.Out);   // en.json changed, de.json is new; nothing else is sent
        var r2 = r1 + 1;
        Assert.Contains($"release #{r2}", second.Out);

        var releases = await Admin("assets", "releases");
        Assert.Contains($"#{r2}", releases.Out);
        Assert.Contains($"← {_channel}", releases.Out);
        var files = await Admin("assets", "release", r2.ToString());
        Assert.Contains("translations/de.json", files.Out);
        Assert.Contains("application/json", files.Out);
        Assert.NotEqual(0, (await Admin("assets", "release", "99999")).Exit);

        // Rolling back asks first, or takes --yes.
        var refused = await Admin("assets", "channels:set", _channel, r1.ToString());
        Assert.Equal(2, refused.Exit);
        _stdin = "y\n";
        var back = await Admin("assets", "channels:set", _channel, r1.ToString());
        Assert.True(back.Exit == 0, back.All);
        Assert.Contains($"#{r2} → #{r1}", back.Out);
        Assert.Contains("  - translations/de.json", back.Out);
        _stdin = null;
        var channels = await Admin("assets", "channels");
        Assert.Matches($"{_channel}\\s+#{r1}", channels.Out);

        var removed = await Admin("assets", "channels:remove", _channel);
        Assert.True(removed.Exit == 0, removed.All);
        Assert.NotEqual(0, (await Admin("assets", "channels:remove", _channel)).Exit);
    }

    [Fact]
    public async Task The_emulator_publishes_the_folder_and_again_on_save()
    {
        Write("assets/translations/en.json", "{\"hello\":\"Hello\"}");
        Write("assets/staff/guide.html", "<p>staff</p>");
        var rules = Path.Combine(_dir, "assets.rules");
        File.WriteAllText(rules, "match translations/{file=**} { allow read; }\n");
        var log = new StringWriter();
        await using var host = await EmulatorHost.StartAsync(new EmulatorOptions
        {
            Port = 0, ConnectionString = fx.ConnectionString, DataDirectory = Path.Combine(_dir, "data"), Quiet = true,
            AssetsRules = rules, Assets = Path.Combine(_dir, "assets"), AssetsChannel = _channel,
        }, log, default);
        Assert.Contains($"on {_channel} (2 files)", log.ToString());

        using var app = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { BaseAddress = new Uri(host.BaseUrl) };
        var listing = await app.GetStringAsync($"/assets/v1/channels/{_channel}");
        Assert.Contains("translations/en.json", listing);
        Assert.DoesNotContain("staff/guide.html", listing);   // the rules don't let anyone list it
        Assert.Equal("{\"hello\":\"Hello\"}", await app.GetStringAsync($"/assets/v1/channels/{_channel}/files/translations/en.json"));
        Assert.Equal(HttpStatusCode.Forbidden, (await app.GetAsync($"/assets/v1/channels/{_channel}/files/staff/guide.html")).StatusCode);

        Write("assets/translations/en.json", "{\"hello\":\"Hi\"}");
        for (var i = 0; i < 50 && !log.ToString().Contains("↻ assets"); i++) await Task.Delay(100);
        Assert.Contains("↻ assets", log.ToString());
        Assert.Equal("{\"hello\":\"Hi\"}", await app.GetStringAsync($"/assets/v1/channels/{_channel}/files/translations/en.json"));
    }
}
