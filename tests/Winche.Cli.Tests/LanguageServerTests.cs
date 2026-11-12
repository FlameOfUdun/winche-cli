using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Winche.Cli.Tests;

/// <summary><c>winche lsp</c>, driven with JSON-RPC messages over in-memory streams.</summary>
public sealed class LanguageServerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("winche-lsp-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Uri(string file) => new Uri(Path.Combine(_dir, file)).AbsoluteUri;

    private static byte[] Frame(JsonNode message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        return [.. Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), .. body];
    }

    private static JsonObject Request(int id, string method, JsonNode? @params = null) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params ?? new JsonObject() };

    private static JsonObject Notify(string method, JsonNode @params) =>
        new() { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params };

    private JsonObject Open(string file, string text) => Notify("textDocument/didOpen", new JsonObject
    {
        ["textDocument"] = new JsonObject { ["uri"] = Uri(file), ["languageId"] = "x", ["version"] = 1, ["text"] = text },
    });

    /// <summary>Sends the messages (plus initialize first and shutdown/exit last) and returns everything the server wrote.</summary>
    private async Task<(int Exit, List<JsonNode> Messages)> Session(params JsonObject[] messages)
    {
        var input = new MemoryStream();
        foreach (var m in new[] { Request(0, "initialize") }.Concat(messages).Concat([Request(999, "shutdown"), Notify("exit", new JsonObject())]))
            input.Write(Frame(m));
        input.Position = 0;
        var output = new MemoryStream();
        var env = new CliEnvironment
        {
            Out = TextWriter.Null, Error = TextWriter.Null, WorkingDirectory = _dir, ConfigDirectory = _dir,
            GetVariable = _ => null, Http = new HttpClientHandler(), OpenBrowser = _ => { },
        };
        using var http = new HttpClient();
        var exit = await new LanguageServer(input, output, env, http).RunAsync(CancellationToken.None);

        var text = Encoding.UTF8.GetString(output.ToArray());
        var list = new List<JsonNode>();
        var at = 0;
        while (at < text.Length)
        {
            var sep = text.IndexOf("\r\n\r\n", at, StringComparison.Ordinal);
            var length = int.Parse(text[(at + "Content-Length: ".Length)..sep]);
            var bodyStart = sep + 4;
            var body = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text[bodyStart..])[..length]);
            list.Add(JsonNode.Parse(body)!);
            at = bodyStart + body.Length;
        }
        return (exit, list);
    }

    private static JsonArray LastDiagnostics(List<JsonNode> messages, string uri) =>
        messages.Last(m => m["method"]?.GetValue<string>() == "textDocument/publishDiagnostics"
                           && m["params"]!["uri"]!.GetValue<string>() == uri)["params"]!["diagnostics"]!.AsArray();

    private static JsonNode Response(List<JsonNode> messages, int id) => messages.Single(m => m["id"]?.GetValue<int>() == id);

    [Fact]
    public async Task Initialize_advertises_capabilities_and_exits_cleanly()
    {
        var (exit, messages) = await Session();
        Assert.Equal(0, exit);
        var caps = Response(messages, 0)["result"]!["capabilities"]!;
        Assert.Equal(1, caps["textDocumentSync"]!.GetValue<int>());
        Assert.True(caps["documentFormattingProvider"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Rules_errors_and_warnings_are_reported_where_they_are()
    {
        var (_, messages) = await Session(
            Open("bad.rules", "match users/{uid} {\n  allow read: if request.auth.uid == ;\n}\n"),
            Open("warn.rules", "match users/{uid} {\n  allow read: if sizee(resource.tags) > 1;\n}\n"),
            Open("deep.rules", "match a/{x=**}/b {\n  match c/{y=**} {\n    allow read;\n  }\n}\n"),
            Open("ok.rules", "match users/{uid} {\n  allow read: if request.auth.uid == uid;\n}\n"));

        var bad = Assert.Single(LastDiagnostics(messages, Uri("bad.rules")));
        Assert.Equal(1, bad!["range"]!["start"]!["line"]!.GetValue<int>());
        Assert.Equal(1, bad["severity"]!.GetValue<int>());

        var warn = Assert.Single(LastDiagnostics(messages, Uri("warn.rules")));
        Assert.Contains("Unknown function 'sizee'", warn!["message"]!.GetValue<string>());
        Assert.Equal(2, warn["severity"]!.GetValue<int>());
        Assert.Equal(17, warn["range"]!["start"]!["character"]!.GetValue<int>());

        var deep = Assert.Single(LastDiagnostics(messages, Uri("deep.rules")));
        Assert.Equal(1, deep!["range"]!["start"]!["line"]!.GetValue<int>());
        Assert.Contains("recursive wildcard", deep["message"]!.GetValue<string>());

        Assert.Empty(LastDiagnostics(messages, Uri("ok.rules")));
    }

    [Fact]
    public async Task Test_files_run_against_their_rules_file_with_lenses()
    {
        File.WriteAllText(Path.Combine(_dir, "database.rules"), "match users/{uid} {\n  allow get: if request.auth.uid == uid;\n}\n");
        const string tests = "test \"own\" {\n  auth { uid: 'u1' }\n  allow get users/u1\n  allow get users/u2\n}\n\ntest \"signed out\" {\n  deny get users/u1\n}\n";

        var (_, messages) = await Session(
            Open("database.rules.test", tests),
            Request(1, "textDocument/codeLens", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = Uri("database.rules.test") } }),
            // The rules file is opened with a fix: the open test file re-runs against the open copy.
            Open("database.rules", "match users/{uid} {\n  allow get: if request.auth.uid != null;\n}\n"));

        var first = messages.First(m => m["method"]?.GetValue<string>() == "textDocument/publishDiagnostics"
                                        && m["params"]!["uri"]!.GetValue<string>() == Uri("database.rules.test"));
        var failure = Assert.Single(first["params"]!["diagnostics"]!.AsArray());
        Assert.Equal(3, failure!["range"]!["start"]!["line"]!.GetValue<int>());
        Assert.Equal("allow get users/u2 — got deny", failure["message"]!.GetValue<string>());

        var lenses = Response(messages, 1)["result"]!.AsArray();
        Assert.Equal(2, lenses.Count);
        Assert.Equal("✗ 1 of 2 checks fails", lenses[0]!["command"]!["title"]!.GetValue<string>());
        Assert.Equal("✓ passes", lenses[1]!["command"]!["title"]!.GetValue<string>());

        Assert.Empty(LastDiagnostics(messages, Uri("database.rules.test")));
    }

    [Fact]
    public async Task Test_syntax_errors_are_reported()
    {
        File.WriteAllText(Path.Combine(_dir, "database.rules"), "match a/{b} { allow read; }");
        var (_, messages) = await Session(Open("database.rules.test", "test \"x\" {\n  allow read a/b\n}\n"));
        var d = Assert.Single(LastDiagnostics(messages, Uri("database.rules.test")));
        Assert.Equal(1, d!["range"]!["start"]!["line"]!.GetValue<int>());
        Assert.Equal(8, d["range"]!["start"]!["character"]!.GetValue<int>());
    }

    [Fact]
    public async Task Formatting_tidies_rules_but_keeps_files_with_comments()
    {
        var (_, messages) = await Session(
            Open("a.rules", "match users/{uid}{allow read:if request.auth.uid==uid;}"),
            Request(1, "textDocument/formatting", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = Uri("a.rules") } }),
            Open("b.rules", "// keep me\nmatch users/{uid}{allow read;}"),
            Request(2, "textDocument/formatting", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = Uri("b.rules") } }));

        var edit = Assert.Single(Response(messages, 1)["result"]!.AsArray());
        Assert.Equal("match users/{uid} {\n  allow read: if request.auth.uid == uid;\n}\n", edit!["newText"]!.GetValue<string>());
        Assert.Empty(Response(messages, 2)["result"]!.AsArray());
        Assert.Contains(messages, m => m["method"]?.GetValue<string>() == "window/showMessage");
    }

    [Fact]
    public async Task Fields_without_a_console_are_empty()
    {
        var (_, messages) = await Session(Request(1, "winche/fields", new JsonObject
        {
            ["uri"] = Uri("database.rules"), ["subsystem"] = "database", ["pattern"] = "users/{uid}",
        }));
        Assert.Empty(Response(messages, 1)["result"]!["fields"]!.AsArray());
    }
}
