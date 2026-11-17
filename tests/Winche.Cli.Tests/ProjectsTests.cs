using Xunit;

namespace Winche.Cli.Tests;

/// <summary><c>winche use</c> and <c>--project</c>: named consoles in winche.json, one picked per checkout.</summary>
[Collection("postgres")]
public sealed class ProjectsTests(PostgresFixture fx) : IAsyncLifetime, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("winche-projects-").FullName;
    private readonly string _configDir = Directory.CreateTempSubdirectory("winche-projects-config-").FullName;
    private ConsoleAppFactory? _app;

    public Task InitializeAsync() => fx.ResetRulesAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _app?.Dispose();
        Directory.Delete(_dir, recursive: true);
        Directory.Delete(_configDir, recursive: true);
    }

    private async Task<(int Exit, string Out, string Err)> Run(params string[] args)
    {
        _app ??= new ConsoleAppFactory(fx);
        var token = await _app.AdminTokenAsync();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await CliApp.RunAsync(args, new CliEnvironment
        {
            Out = stdout, Error = stderr, WorkingDirectory = _dir, ConfigDirectory = _configDir,
            GetVariable = name => name == "WINCHE_TOKEN" ? token : null,
            Http = _app.Server.CreateHandler(), OpenBrowser = _ => { },
        });
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private void Write(string file, string text) => File.WriteAllText(Path.Combine(_dir, file), text);

    [Fact]
    public async Task Use_adds_lists_picks_and_removes_projects()
    {
        Write("winche.json", """{ "rules": { "database": "database.rules" } }""");

        Assert.Contains("No projects", (await Run("use")).Out);
        Assert.Contains("Added local (http://localhost/projects/demo) and using it here.", (await Run("use", "--add", "local", "http://localhost/projects/demo")).Out);
        Assert.Contains("Added prod", (await Run("use", "--add", "prod", "https://api.example.com/projects/clinic/")).Out);
        Assert.Contains("\"prod\": \"https://api.example.com/projects/clinic\"", File.ReadAllText(Path.Combine(_dir, "winche.json")));
        // An address of Winche Server before 3.0 is refused, with what to write.
        var old = await Run("use", "--add", "old", "https://api.example.com/p/clinic/_console");
        Assert.Equal(1, old.Exit);
        Assert.Contains("https://host/projects/<id>", old.Err);

        var list = (await Run("use")).Out;
        Assert.Contains("* prod", list);
        Assert.Contains("  local", list);

        Assert.Contains("Now using local", (await Run("use", "local")).Out);
        Assert.Contains("* local", (await Run("use")).Out);

        var missing = await Run("use", "staging");
        Assert.Equal(1, missing.Exit);
        Assert.Contains("No project named 'staging' (have local, prod)", missing.Err);

        Assert.Contains("Removed local", (await Run("use", "--remove", "local")).Out);
        Assert.Contains("(the only project is used)", (await Run("use")).Out);
    }

    [Fact]
    public async Task Commands_go_to_the_picked_project_and_say_which()
    {
        Write("winche.json", """
            { "projects": { "local": "http://localhost/projects/demo", "prod": "https://prod.invalid/projects/demo" },
              "rules": { "database": "database.rules" } }
            """);
        Write("database.rules", "match a/{b} { allow read; }");

        var unpicked = await Run("rules", "deploy");
        Assert.Equal(1, unpicked.Exit);
        Assert.Contains("Which project? Run winche use <alias> (local, prod)", unpicked.Err);

        await Run("use", "local");
        var deploy = await Run("rules", "deploy");
        Assert.True(deploy.Exit == 0, deploy.Err);
        Assert.Contains("Project: local (http://localhost/projects/demo)", deploy.Out);
        Assert.Contains("deployed database.rules as v1", deploy.Out);

        // --project overrides the pick (the test server answers every host).
        var prod = await Run("rules", "history", "-P", "prod");
        Assert.Contains("Project: prod (https://prod.invalid/projects/demo)", prod.Out);

        var unknown = await Run("rules", "history", "--project", "nope");
        Assert.Contains("--project nope: no such project (have local, prod)", unknown.Err);

        // --console beats both.
        Assert.DoesNotContain("Project:", (await Run("rules", "history", "--console", "http://localhost/projects/demo")).Out);
    }

    [Fact]
    public async Task The_pick_is_per_checkout()
    {
        Write("winche.json", """{ "projects": { "a": "http://localhost/projects/demo", "b": "http://localhost/projects/demo" } }""");
        await Run("use", "a");
        var other = Directory.CreateTempSubdirectory("winche-projects-other-").FullName;
        try
        {
            File.Copy(Path.Combine(_dir, "winche.json"), Path.Combine(other, "winche.json"));
            var stdout = new StringWriter();
            await CliApp.RunAsync(["use"], new CliEnvironment
            {
                Out = stdout, Error = TextWriter.Null, WorkingDirectory = other, ConfigDirectory = _configDir,
                GetVariable = _ => null, Http = new HttpClientHandler(), OpenBrowser = _ => { },
            });
            Assert.Contains("(none picked", stdout.ToString());
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
        Assert.Contains("* a", (await Run("use")).Out);
    }
}

/// <summary>Addresses on a Winche Server: the server's, a project's (<c>/projects/&lt;id&gt;</c>), and their console API.</summary>
public sealed class ServerProjectTargetTests
{
    [Theory]
    [InlineData("https://api.example.com", "https://api.example.com/console/v1/", null)]
    [InlineData("https://api.example.com/projects/clinic/", "https://api.example.com/console/v1/projects/clinic/", "clinic")]
    [InlineData("https://example.com/winche", "https://example.com/winche/console/v1/", null)]   // a server behind a path
    [InlineData("https://example.com/winche/projects/a", "https://example.com/winche/console/v1/projects/a/", "a")]
    [InlineData("https://api.example.com/console/v1/projects/clinic", "https://api.example.com/console/v1/projects/clinic/", "clinic")]
    public void Addresses_people_write_become_the_console_api(string written, string handle, string? project)
    {
        var parsed = ConsoleTarget.Parse(written);
        Assert.Equal(handle, parsed.ToString());
        Assert.Equal(project, ConsoleAddress.ProjectOf(parsed));
        Assert.Equal(written.TrimEnd('/').Replace("/console/v1", ""), ConsoleAddress.Describe(parsed));
    }

    [Theory]
    [InlineData("https://api.example.com/_console")]
    [InlineData("https://api.example.com/p/clinic/_console")]
    [InlineData("ftp://api.example.com")]
    public void Old_and_unknown_addresses_are_refused(string written) => Assert.Throws<CliException>(() => ConsoleTarget.Parse(written));

    [Fact]
    public void Calls_go_to_the_server_or_the_project_by_area()
    {
        var project = ConsoleTarget.Parse("https://api.example.com/projects/clinic");
        Assert.Equal("https://api.example.com/console/v1/projects/clinic/rules/database/live", ConsoleAddress.Url(project, "rules/database/live").ToString());
        Assert.Equal("https://api.example.com/console/v1/projects/clinic/tokens", ConsoleAddress.Url(project, "tokens").ToString());
        Assert.Equal("https://api.example.com/console/v1/session/state", ConsoleAddress.Url(project, "session/state").ToString());
        Assert.Equal("https://api.example.com/console/v1/projects", ConsoleAddress.Url(project, "projects").ToString());
        Assert.Equal("https://api.example.com/console/v1/projects/wards/", ConsoleTarget.ForProject(project, "wards").ToString());
        Assert.Equal("https://api.example.com/console/", ConsoleAddress.PagesOf(project).ToString());
        // A project's area without a project: pick one.
        Assert.Contains("--project", Assert.Throws<CliException>(() => ConsoleAddress.Url(ConsoleTarget.Parse("https://api.example.com"), "rules/database/live")).Message);
    }

    [Fact]
    public void Project_is_an_alias_first_else_an_id_on_the_server_otherwise_used()
    {
        static CliEnvironment Env(Func<string, string?> vars) => new()
        {
            Out = TextWriter.Null, Error = TextWriter.Null, WorkingDirectory = Path.GetTempPath(), ConfigDirectory = Path.GetTempPath(),
            GetVariable = vars, Http = new HttpClientHandler(), OpenBrowser = _ => { },
        };
        var env = Env(name => name == "WINCHE_CONSOLE" ? "https://api.example.com" : null);
        var config = new ProjectConfig { Projects = new() { ["prod"] = "https://prod.example.com/projects/clinic" } };
        Assert.Equal("https://prod.example.com/console/v1/projects/clinic/", ConsoleTarget.Resolve(config, null, "prod", env)!.Url.ToString());
        Assert.Throws<CliException>(() => ConsoleTarget.Resolve(config, null, "wards", env));   // with aliases, an unknown name is a typo
        Assert.Equal("https://api.example.com/console/v1/projects/wards/", ConsoleTarget.Resolve(new ProjectConfig(), null, "wards", env)!.Url.ToString());
        var nowhere = Env(_ => null);
        Assert.Throws<CliException>(() => ConsoleTarget.Resolve(new ProjectConfig(), null, "wards", nowhere));
        // winche.json: "server" and "project"; an old "console" is refused with what to write.
        Assert.Equal("https://api.example.com/console/v1/projects/clinic/",
            ConsoleTarget.Resolve(new ProjectConfig { Server = "https://api.example.com", Project = "clinic" }, null, null, nowhere)!.Url.ToString());
        Assert.Contains("\"server\"", Assert.Throws<CliException>(() => ConsoleTarget.Resolve(new ProjectConfig { Console = "https://api.example.com/_console" }, null, null, nowhere)).Message);
    }
}
