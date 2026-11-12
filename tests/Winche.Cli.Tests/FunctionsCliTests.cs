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

    private async Task<(int Exit, string Out, string Err)> Run(HttpMessageHandler http, Func<string, string?>? vars, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await CliApp.RunAsync(args, new CliEnvironment
        {
            Out = stdout, Error = stderr, WorkingDirectory = _dir, ConfigDirectory = _dir,
            GetVariable = vars ?? (_ => null), Http = http, OpenBrowser = _ => { },
        });
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task Init_makes_a_folder_vs_code_understands_and_deploy_sends_it_with_the_key()
    {
        File.WriteAllText(Path.Combine(_dir, "winche.json"), """{ "rules": {} }""");
        var (exit, output, _) = await Run(new Recorder(), null, "functions", "init");
        Assert.Equal(0, exit);
        foreach (var f in new[] { "index.ts", "deno.json", ".winche/functions.ts", ".vscode/settings.json" })
            Assert.True(File.Exists(Path.Combine(_dir, "functions", f)), f);
        Assert.Contains("@winche/functions", File.ReadAllText(Path.Combine(_dir, "functions", "deno.json")));
        Assert.Equal("functions", JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "winche.json")))!["functions"]!.GetValue<string>());
        Assert.Contains("added \"functions\"", output);
        File.WriteAllText(Path.Combine(_dir, "functions", "notes.md"), "not code");

        var server = new Recorder();
        (exit, output, var err) = await Run(server, v => v == "WINCHE_DEPLOY_KEY" ? "wfd_key" : null, "functions", "deploy", "--url", "https://api.example.com", "-m", "From the laptop");
        Assert.True(exit == 0, err);
        Assert.Equal("https://api.example.com/functions/v1/deploy", server.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer wfd_key", server.Request.Headers.Authorization!.ToString());
        var sent = JsonNode.Parse(server.Body!)!;
        Assert.Equal(["deno.json", "index.ts"], sent["files"]!.AsObject().Select(kv => kv.Key).Order());   // not .winche/, .vscode/ or notes
        Assert.Equal("From the laptop", sent["note"]!.GetValue<string>());
        Assert.Contains("Deployed version 4: hello.", output);

        var refusing = new Recorder { Answer = HttpStatusCode.BadRequest, Reply = """{ "error": { "message": "The code doesn't build.", "problems": ["index.ts:3: Expected ';'"] } }""" };
        (exit, _, err) = await Run(refusing, null, "functions", "deploy", "--url", "https://api.example.com", "--key", "k");
        Assert.Equal(1, exit);
        Assert.Contains("Not deployed: The code doesn't build.", err);
        Assert.Contains("Expected ';'", err);
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
    }
}
