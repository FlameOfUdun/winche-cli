using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Winche.Cli.Tests;

/// <summary>Fixes from a server set up by hand: the Windows sign-in link, a bucket with the server's own AWS rights, no projects.</summary>
public class ReportedFixesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("winche-cli-fixes-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void The_sign_in_link_reaches_the_browser_whole()
    {
        const string url = "https://platform.example.com/console/cli-login?port=9005&state=abc&challenge=x%2By";
        var windows = Browser.Command(url, windows: true, mac: false);
        Assert.Equal((url, true, ""), (windows.FileName, windows.UseShellExecute, windows.Arguments));   // no cmd, nothing escaped
        var mac = Browser.Command(url, windows: false, mac: true);
        Assert.Equal(("open", false), (mac.FileName, mac.UseShellExecute));
        Assert.Equal([url], mac.ArgumentList);
        Assert.Equal([url], Browser.Command(url, windows: false, mac: false).ArgumentList);
    }

    private sealed class FakeConsole(Func<HttpMethod, string, string?, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));
            var (status, text) = answer(request.Method, request.RequestUri.PathAndQuery, body);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    private async Task<(int Exit, string Out, string Err)> Run(HttpMessageHandler http, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await CliApp.RunAsync(args, new CliEnvironment
        {
            Out = stdout, Error = stderr, WorkingDirectory = _dir, ConfigDirectory = _dir, Http = http, OpenBrowser = _ => { },
            GetVariable = v => v == "WINCHE_TOKEN" ? "wsa_ci" : null,
        });
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task Moving_files_to_a_bucket_without_an_access_key_asks_for_no_secret()
    {
        File.WriteAllText(Path.Combine(_dir, "winche.json"), """{ "server": "https://api.example.com", "project": "crm" }""");
        var moved = false;
        var console = new FakeConsole((method, path, _) => (method.Method, path) switch
        {
            ("GET", "/console/v1/projects/crm/settings/location/files") => (HttpStatusCode.OK,
                moved ? """{ "activeLabel": "On this server's disk", "move": { "phase": "ready", "toLabel": "bucket crm-files" }, "progress": { "copied": 3, "alreadyThere": 0, "total": 3, "failed": 0 } }"""
                      : """{ "activeLabel": "On this server's disk", "move": null }"""),
            ("POST", "/console/v1/projects/crm/settings/location/files/move") => (moved = true) ? (HttpStatusCode.Accepted, "{}") : default,
            ("POST", "/console/v1/projects/crm/settings/location/files/move/switch") => (HttpStatusCode.OK, "{}"),
            _ => (HttpStatusCode.NotFound, "{}"),
        });
        var (exit, output, err) = await Run(console, "storage:move", "--bucket", "crm-files", "--region", "eu-west-2", "--yes");
        Assert.True(exit == 0, err);   // in CI, with no WINCHE_SECRET: no secret asked for
        var sent = JsonNode.Parse(console.Requests.Single(r => r.Path.EndsWith("/files/move")).Body!)!;
        Assert.Null(sent["location"]!["s3"]!["accessKey"]);
        Assert.Null(sent["secretKey"]);
        Assert.Contains("the project now uses the new location", output);

        // With an access key, its secret is still needed.
        moved = false;
        (exit, _, err) = await Run(console, "storage:move", "--bucket", "crm-files", "--region", "eu-west-2", "--access-key", "AKIA1", "--yes");
        Assert.NotEqual(0, exit);
        Assert.Contains("WINCHE_SECRET", err);
    }

    [Fact]
    public async Task A_server_with_no_projects_says_how_to_make_one()
    {
        var console = new FakeConsole((_, path, _) => path == "/console/v1/projects"
            ? (HttpStatusCode.OK, """{ "available": true, "current": null, "projects": [], "archived": [] }""") : (HttpStatusCode.NotFound, "{}"));
        var (exit, output, err) = await Run(console, "projects", "--console", "https://platform.example.com");
        Assert.True(exit == 0, err);
        Assert.Contains("has no projects yet. Make one (server admins): winche projects:create", output);
    }
}
