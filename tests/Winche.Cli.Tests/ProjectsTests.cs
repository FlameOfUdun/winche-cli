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
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await CliApp.RunAsync(args, new CliEnvironment
        {
            Out = stdout, Error = stderr, WorkingDirectory = _dir, ConfigDirectory = _configDir,
            GetVariable = name => name == "WINCHE_TOKEN" ? KeycloakTokens.Mint(["Admin"]) : null,
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
        Assert.Contains("Added local (http://localhost/_console) and using it here.", (await Run("use", "--add", "local", "http://localhost/_console")).Out);
        Assert.Contains("Added prod", (await Run("use", "--add", "prod", "https://api.example.com/_console/")).Out);
        Assert.Contains("\"prod\": \"https://api.example.com/_console\"", File.ReadAllText(Path.Combine(_dir, "winche.json")));

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
            { "projects": { "local": "http://localhost/_console", "prod": "https://prod.invalid/_console" },
              "rules": { "database": "database.rules" } }
            """);
        Write("database.rules", "match a/{b} { allow read; }");

        var unpicked = await Run("rules", "deploy");
        Assert.Equal(1, unpicked.Exit);
        Assert.Contains("Which project? Run winche use <alias> (local, prod)", unpicked.Err);

        await Run("use", "local");
        var deploy = await Run("rules", "deploy");
        Assert.True(deploy.Exit == 0, deploy.Err);
        Assert.Contains("Project: local (http://localhost/_console)", deploy.Out);
        Assert.Contains("deployed database.rules as v1", deploy.Out);

        // --project overrides the pick (the test server answers every host).
        var prod = await Run("rules", "history", "-P", "prod");
        Assert.Contains("Project: prod (https://prod.invalid/_console)", prod.Out);

        var unknown = await Run("rules", "history", "--project", "nope");
        Assert.Contains("--project nope: no such project (have local, prod)", unknown.Err);

        // --console beats both.
        Assert.DoesNotContain("Project:", (await Run("rules", "history", "--console", "http://localhost/_console")).Out);
    }

    [Fact]
    public async Task The_pick_is_per_checkout()
    {
        Write("winche.json", """{ "projects": { "a": "http://localhost/_console", "b": "http://localhost/_console" } }""");
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
