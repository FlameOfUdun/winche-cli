using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Winche.Cli.Tests;

/// <summary>winche plugins…, and functions deploy with winche.json's plugins.</summary>
public class PluginsCliTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("winche-cli-plugins-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeConsole : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add((request.Method, path, body));
            (HttpStatusCode, string) answer = (request.Method.Method, path) switch
            {
                ("GET", "/console/v1/projects/demo/functions/plugins") or ("GET", "/console/v1/projects/demo/functions/plugins?updates=false") => (HttpStatusCode.OK, """
                    { "plugins": [{ "id": "sms", "package": "text-plugin", "version": "1.0.0", "latest": "1.1.0", "update": true, "source": "console",
                                    "settings": { "FROM": "+44" }, "functions": ["sms.send"], "secretsNeeded": ["SMS__AUTH_TOKEN"] }] }
                    """),
                ("POST", "/console/v1/projects/demo/functions/plugins") => (HttpStatusCode.OK, """{ "version": 3, "note": "Installed alerts (text-plugin 1.1.0)" }"""),
                ("DELETE", "/console/v1/projects/demo/functions/plugins/sms?secrets=remove") => (HttpStatusCode.OK, """{ "version": 4, "note": "Removed sms (text-plugin)" }"""),
                ("POST", "/console/v1/projects/demo/functions/deploy") => (HttpStatusCode.OK, """{ "version": 5, "functions": [{ "name": "alerts.send" }] }"""),
                _ => (HttpStatusCode.NotFound, "{}"),
            };
            return new HttpResponseMessage(answer.Item1) { Content = new StringContent(answer.Item2, Encoding.UTF8, "application/json") };
        }
    }

    private async Task<(int Exit, string Out, string Err)> Run(HttpMessageHandler http, string? stdin, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await CliApp.RunAsync(args, new CliEnvironment
        {
            Out = stdout, Error = stderr, WorkingDirectory = _dir, ConfigDirectory = _dir, Http = http, OpenBrowser = _ => { },
            GetVariable = v => v == "WINCHE_TOKEN" ? "wsa_ci" : null,
            In = new StringReader(stdin ?? ""), Interactive = stdin is not null,
        });
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task Plugins_are_listed_installed_and_removed()
    {
        File.WriteAllText(Path.Combine(_dir, "winche.json"), """{ "server": "https://api.example.com", "project": "demo" }""");
        var console = new FakeConsole();
        var (exit, output, _) = await Run(console, null, "plugins");
        Assert.Equal(0, exit);
        Assert.Contains("sms  text-plugin 1.0.0 (1.1.0 is out: winche plugins:upgrade sms)  from the console", output);
        Assert.Contains("needs the secret SMS__AUTH_TOKEN: winche functions secrets:set SMS__AUTH_TOKEN", output);

        (exit, output, _) = await Run(console, null, "plugins:install", "text-plugin@1.1.0", "--id", "alerts", "--set", "FROM=+1", "--set", "RETRIES=2", "--set", "LOUD=true");
        Assert.Equal((0, "Functions version 3: Installed alerts (text-plugin 1.1.0)."), (exit, output.Trim()));
        var sent = JsonNode.Parse(console.Requests.Last().Body!)!;
        Assert.Equal(("alerts", "text-plugin", "1.1.0"), (sent["id"]!.GetValue<string>(), sent["package"]!.GetValue<string>(), sent["version"]!.GetValue<string>()));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "FROM": "+1", "RETRIES": 2, "LOUD": true }"""), sent["settings"]), sent["settings"]!.ToJsonString());
        Assert.Equal(("@acme/x", "2.0.0"), CliApp.Run.Spec("@acme/x@2.0.0"));
        Assert.Equal(("@acme/x", null), CliApp.Run.Spec("@acme/x"));

        (exit, output, _) = await Run(console, null, "plugins:uninstall", "sms", "--remove-secrets");
        Assert.Equal((0, "Functions version 4: Removed sms (text-plugin)."), (exit, output.Trim()));
    }

    [Fact]
    public async Task A_deploy_with_winche_jsons_plugins_asks_before_changing_ones_installed_in_the_console()
    {
        File.WriteAllText(Path.Combine(_dir, "winche.json"), """
            { "server": "https://api.example.com", "project": "demo", "functions": "functions",
              "plugins": { "alerts": { "package": "text-plugin", "version": "1.1.0", "settings": { "FROM": "+1" } } } }
            """);
        Directory.CreateDirectory(Path.Combine(_dir, "functions"));
        File.WriteAllText(Path.Combine(_dir, "functions", "index.ts"), "export {};");
        var console = new FakeConsole();

        // In CI nobody can answer: refused, saying what it would remove.
        var (exit, _, err) = await Run(console, null, "functions", "deploy");
        Assert.NotEqual(0, exit);
        Assert.Contains("- sms (text-plugin 1.0.0) is removed", err);
        Assert.Contains("--force", err);
        Assert.DoesNotContain(console.Requests, r => r.Path.EndsWith("/deploy"));

        // Asked, and declined; then accepted.
        (exit, _, _) = await Run(console, "n\n", "functions", "deploy");
        Assert.NotEqual(0, exit);
        (exit, var output, _) = await Run(console, "y\n", "functions", "deploy");
        Assert.Equal(0, exit);
        Assert.Contains("Deployed version 5: alerts.send", output);
        var plugins = JsonNode.Parse(console.Requests.Last().Body!)!["plugins"]!.AsArray();
        Assert.Equal("alerts", Assert.Single(plugins)!["id"]!.GetValue<string>());
    }
}
