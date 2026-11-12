using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Winche.Rules;
using Winche.Rules.Evaluation;
using Winche.Rules.Expressions;
using Winche.Rules.Text;

namespace Winche.Cli;

/// <summary>
/// <c>winche lsp</c>: a Language Server Protocol server over stdin/stdout for <c>.rules</c> and
/// <c>.rules.test</c> files (the Winche VS Code extension starts it). It reports the same problems as
/// <c>winche rules validate</c> / <c>rules test</c> as you type, formats rules files, shows each test's
/// result above it, and answers <c>winche/fields</c> with the document fields the console samples for a
/// match pattern (when signed in with <c>winche login</c>).
/// </summary>
internal sealed class LanguageServer(Stream input, Stream output, CliEnvironment env, HttpClient http)
{
    private readonly ConcurrentDictionary<string, string> _open = new(StringComparer.Ordinal);   // uri → text
    private readonly Dictionary<string, (DateTimeOffset At, JsonArray Fields)> _fieldCache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _write = new(1, 1);
    private bool _shutdown;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var message = await ReadMessageAsync(ct);
            if (message is null) return _shutdown ? 0 : 1;   // stdin closed
            var method = message["method"]?.GetValue<string>();
            var id = message["id"];
            var @params = message["params"];
            try
            {
                switch (method)
                {
                    case "initialize":
                        await RespondAsync(id, Capabilities());
                        break;
                    case "shutdown":
                        _shutdown = true;
                        await RespondAsync(id, null);
                        break;
                    case "exit":
                        return _shutdown ? 0 : 1;
                    case "textDocument/didOpen":
                        await OpenedOrChangedAsync(@params!["textDocument"]!["uri"]!.GetValue<string>(),
                            @params["textDocument"]!["text"]!.GetValue<string>());
                        break;
                    case "textDocument/didChange":
                        var changes = @params!["contentChanges"]!.AsArray();
                        await OpenedOrChangedAsync(@params["textDocument"]!["uri"]!.GetValue<string>(),
                            changes[^1]!["text"]!.GetValue<string>());
                        break;
                    case "textDocument/didClose":
                        var closed = @params!["textDocument"]!["uri"]!.GetValue<string>();
                        _open.TryRemove(closed, out _);
                        await PublishAsync(closed, []);
                        break;
                    case "textDocument/formatting":
                        await RespondAsync(id, Format(@params!["textDocument"]!["uri"]!.GetValue<string>()));
                        break;
                    case "textDocument/codeLens":
                        await RespondAsync(id, await CodeLensesAsync(@params!["textDocument"]!["uri"]!.GetValue<string>(), ct));
                        break;
                    case "winche/fields":
                        await RespondAsync(id, await FieldsAsync(@params!, ct));
                        break;
                    default:
                        if (id is not null) await RespondErrorAsync(id, -32601, $"Method not found: {method}");
                        break;   // notifications we don't handle ($/cancelRequest, initialized, didSave, …)
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (id is not null) await RespondErrorAsync(id, -32603, ex.Message);
                else await NotifyAsync("window/logMessage", new JsonObject { ["type"] = 1, ["message"] = ex.ToString() });
            }
        }
        return 0;
    }

    private static JsonObject Capabilities() => new()
    {
        ["capabilities"] = new JsonObject
        {
            ["textDocumentSync"] = 1,   // full
            ["documentFormattingProvider"] = true,
            ["codeLensProvider"] = new JsonObject { ["resolveProvider"] = false },
        },
        ["serverInfo"] = new JsonObject { ["name"] = "winche", ["version"] = Version() },
    };

    private static string Version() =>
        typeof(LanguageServer).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "?";

    // ----- diagnostics -----------------------------------------------------------------------

    private async Task OpenedOrChangedAsync(string uri, string text)
    {
        _open[uri] = text;
        if (IsTestFile(uri))
        {
            await PublishAsync(uri, await TestDiagnosticsAsync(uri, text));
            return;
        }
        if (!IsRulesFile(uri)) return;
        await PublishAsync(uri, RulesDiagnostics(text));
        // Open test files that test this rules file re-run against the new text.
        foreach (var (testUri, testText) in _open)
            if (IsTestFile(testUri) && string.Equals(PairedRules(testUri)?.Path, UriToPath(uri), StringComparison.Ordinal))
                await PublishAsync(testUri, await TestDiagnosticsAsync(testUri, testText));
    }

    internal static JsonArray RulesDiagnostics(string text)
    {
        var list = new JsonArray();
        RuleSet ruleSet;
        try
        {
            ruleSet = RuleText.ParseRuleSet(text);
        }
        catch (RuleSyntaxException ex)
        {
            list.Add(Diagnostic(text, ex.Line - 1, ex.Column - 1, ex.Reason, 1));
            return list;
        }
        catch (ArgumentException ex)
        {
            list.Add(Diagnostic(text, 0, 0, ex.Message, 1));
            return list;
        }

        foreach (var issue in RuleSetValidation.Validate(ruleSet))
        {
            var (line, col) = FindMatch(text, issue.Path);
            list.Add(Diagnostic(text, line, col, issue.Message, 1));
        }
        foreach (var name in UnknownFunctions(ruleSet))
            foreach (var (line, col) in Occurrences(text, name + "("))
                list.Add(Diagnostic(text, line, col, $"Unknown function '{name}' — it will always deny.", 2, name.Length));
        return list;
    }

    private async Task<JsonArray> TestDiagnosticsAsync(string uri, string text)
    {
        var list = new JsonArray();
        try
        {
            RuleTestParser.Parse(text);
        }
        catch (RuleTestSyntaxException ex)
        {
            list.Add(Diagnostic(text, ex.Line - 1, ex.Column - 1, ex.Message, 1));
            return list;
        }

        var outcomes = await RunTestsAsync(uri, text);
        if (outcomes.Error is { } error)
        {
            list.Add(Diagnostic(text, 0, 0, error, 2));
            return list;
        }
        foreach (var o in outcomes.Results.Where(o => !o.Passed))
            list.Add(Diagnostic(text, o.Check.Line - 1, FirstNonSpace(text, o.Check.Line - 1),
                $"{o.Check.Describe()} — got {(o.Allowed ? "allow" : "deny")}", 1, LineLength(text, o.Check.Line - 1)));
        return list;
    }

    private sealed record TestRun(IReadOnlyList<RuleTests.Outcome> Results, string? Error);

    /// <summary>Runs a test file's checks against its rules file (the open copy if there is one).</summary>
    private async Task<TestRun> RunTestsAsync(string uri, string text)
    {
        var paired = PairedRules(uri);
        if (paired is null) return new([], "No rules file for these tests: name it X.rules next to X.rules.test, or list both in winche.json.");
        var (rulesPath, subsystem) = paired.Value;
        var rulesText = _open.FirstOrDefault(kv => UriToPath(kv.Key) == rulesPath).Value
                        ?? (File.Exists(rulesPath) ? await File.ReadAllTextAsync(rulesPath) : null);
        if (rulesText is null) return new([], $"{Path.GetFileName(rulesPath)} not found.");

        RuleSet ruleSet;
        try
        {
            ruleSet = RuleText.ParseRuleSet(rulesText);
        }
        catch (Exception ex) when (ex is RuleSyntaxException or ArgumentException)
        {
            return new([], $"{Path.GetFileName(rulesPath)} has an error, so the tests can't run.");
        }

        try
        {
            return new(await RuleTests.RunTextAsync(subsystem, ruleSet, text, CancellationToken.None), null);
        }
        catch (RuleTestSyntaxException)
        {
            return new([], null);
        }
    }

    /// <summary>The rules file a test file tests: from winche.json's "tests", else X.rules next to X.rules.test.</summary>
    internal static (string Path, string Subsystem)? PairedRules(string testUri)
    {
        var testPath = UriToPath(testUri);
        var dir = Path.GetDirectoryName(testPath)!;
        try
        {
            if (ProjectConfig.Find(dir, null) is { } config)
                foreach (var sys in ProjectConfig.Subsystems)
                    if (config.TestsPath(sys) == testPath && config.Rules.ContainsKey(sys))
                        return (config.RulesPath(sys), sys);
        }
        catch (CliException)
        {
            // An invalid winche.json: fall back to the naming convention.
        }
        if (!testPath.EndsWith(".rules.test", StringComparison.Ordinal)) return null;
        var rules = testPath[..^".test".Length];
        var subsystem = Path.GetFileName(rules).StartsWith("storage", StringComparison.OrdinalIgnoreCase) ? "storage" : "database";
        return (rules, subsystem);
    }

    // ----- code lenses: each test's result ---------------------------------------------------

    private async Task<JsonArray> CodeLensesAsync(string uri, CancellationToken ct)
    {
        var lenses = new JsonArray();
        if (!IsTestFile(uri) || !_open.TryGetValue(uri, out var text)) return lenses;
        try { RuleTestParser.Parse(text); }
        catch (RuleTestSyntaxException) { return lenses; }

        var run = await RunTestsAsync(uri, text);
        if (run.Error is not null) return lenses;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (var group in run.Results.GroupBy(o => o.Check.Test))
        {
            var line = Array.FindIndex(lines, l => l.TrimStart().StartsWith("test", StringComparison.Ordinal)
                                                   && l.Contains($"\"{group.Key}\"", StringComparison.Ordinal));
            if (line < 0) continue;
            var failed = group.Count(o => !o.Passed);
            var total = group.Count();
            lenses.Add(new JsonObject
            {
                ["range"] = Range(line, 0, line, 0),
                ["command"] = new JsonObject
                {
                    ["title"] = failed == 0
                        ? $"✓ {(total == 1 ? "passes" : $"all {total} checks pass")}"
                        : $"✗ {failed} of {total} check{(total == 1 ? "" : "s")} fail{(failed == 1 ? "s" : "")}",
                    ["command"] = "winche.rules.test",
                },
            });
        }
        _ = ct;
        return lenses;
    }

    // ----- formatting --------------------------------------------------------------------------

    private JsonNode? Format(string uri)
    {
        if (!IsRulesFile(uri) || !_open.TryGetValue(uri, out var text)) return null;
        if (HasComment(text))
        {
            _ = NotifyAsync("window/showMessage", new JsonObject
            {
                ["type"] = 3,
                ["message"] = "Winche: formatting would drop this file's comments (rules are stored as data), so it was left as it is.",
            });
            return new JsonArray();
        }
        string formatted;
        try { formatted = RuleText.FormatRuleSet(RuleText.ParseRuleSet(text)); }
        catch (Exception ex) when (ex is RuleSyntaxException or ArgumentException) { return new JsonArray(); }
        if (!formatted.EndsWith('\n')) formatted += "\n";
        var lineCount = text.Split('\n').Length;
        return new JsonArray(new JsonObject { ["range"] = Range(0, 0, lineCount, 0), ["newText"] = formatted });
    }

    private static bool HasComment(string text)
    {
        var inString = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString != '\0')
            {
                if (c == '\\') i++;
                else if (c == inString || c == '\n') inString = '\0';
            }
            else if (c is '\'' or '"' or '`') inString = c;
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') return true;
        }
        return false;
    }

    // ----- winche/fields ----------------------------------------------------------------------

    /// <summary><c>{ uri, subsystem, pattern }</c> → <c>{ fields: [{ path, types }] }</c>; empty when not signed in.</summary>
    private async Task<JsonObject> FieldsAsync(JsonNode p, CancellationToken ct)
    {
        var empty = new JsonObject { ["fields"] = new JsonArray() };
        var uri = p["uri"]?.GetValue<string>();
        var subsystem = p["subsystem"]?.GetValue<string>() ?? "database";
        var pattern = p["pattern"]?.GetValue<string>() ?? "";
        if (uri is null || !ProjectConfig.Subsystems.Contains(subsystem)) return empty;

        ProjectConfig? config;
        try { config = ProjectConfig.Find(Path.GetDirectoryName(UriToPath(uri))!, null); }
        catch (CliException) { return empty; }
        var consoleUrl = env.GetVariable("WINCHE_CONSOLE") ?? config?.Console;
        if (consoleUrl is null || !Uri.TryCreate(consoleUrl.TrimEnd('/') + "/", UriKind.Absolute, out var console)) return empty;

        var key = $"{console}|{subsystem}|{pattern}";
        lock (_fieldCache)
            if (_fieldCache.TryGetValue(key, out var hit) && hit.At > env.Time.GetUtcNow().AddMinutes(-2))
                return new JsonObject { ["fields"] = hit.Fields.DeepClone() };

        var token = env.GetVariable("WINCHE_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            var store = new CredentialStore(env.ConfigDirectory);
            if (store.Get(console) is not { } saved) return empty;
            var fresh = await new KeycloakLogin(env, http).RefreshIfNeededAsync(saved, ct);
            if (fresh is null) return empty;
            if (!ReferenceEquals(fresh, saved)) store.Put(console, fresh);
            token = fresh.AccessToken;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(console, $"api/rules/{subsystem}/fields?pattern={Uri.EscapeDataString(pattern)}"));
            request.Headers.Authorization = new("Bearer", token);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/json") return empty;
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            if (body?["fields"] is not JsonArray fields) return empty;
            lock (_fieldCache) _fieldCache[key] = (env.Time.GetUtcNow(), (JsonArray)fields.DeepClone());
            return new JsonObject { ["fields"] = fields.DeepClone() };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return empty;
        }
    }

    // ----- helpers -----------------------------------------------------------------------------

    private static bool IsTestFile(string uri) => uri.EndsWith(".rules.test", StringComparison.Ordinal);
    private static bool IsRulesFile(string uri) => uri.EndsWith(".rules", StringComparison.Ordinal);

    internal static string UriToPath(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.IsFile ? Path.GetFullPath(u.LocalPath) : uri;

    private static JsonObject Range(int l1, int c1, int l2, int c2) => new()
    {
        ["start"] = new JsonObject { ["line"] = l1, ["character"] = c1 },
        ["end"] = new JsonObject { ["line"] = l2, ["character"] = c2 },
    };

    private static JsonObject Diagnostic(string text, int line, int col, string message, int severity, int? length = null)
    {
        line = Math.Max(0, line);
        col = Math.Max(0, col);
        var len = length ?? TokenLength(text, line, col);
        return new JsonObject
        {
            ["range"] = Range(line, col, line, col + Math.Max(1, len)),
            ["severity"] = severity,
            ["source"] = "winche",
            ["message"] = message,
        };
    }

    private static string? Line(string text, int line)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return line < lines.Length ? lines[line] : null;
    }

    private static int TokenLength(string text, int line, int col)
    {
        var l = Line(text, line);
        if (l is null || col >= l.Length) return 1;
        var end = col;
        while (end < l.Length && (char.IsLetterOrDigit(l[end]) || l[end] == '_')) end++;
        return Math.Max(1, end - col);
    }

    private static int FirstNonSpace(string text, int line)
    {
        var l = Line(text, line) ?? "";
        return l.Length - l.TrimStart().Length;
    }

    private static int LineLength(string text, int line)
    {
        var l = Line(text, line) ?? "";
        return l.TrimEnd().Length - FirstNonSpace(text, line);
    }

    /// <summary>Where the match block whose full path is <paramref name="fullPath"/> starts.</summary>
    private static (int Line, int Col) FindMatch(string text, string? fullPath)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("match ", StringComparison.Ordinal)) continue;
            var own = trimmed[6..].Split('{', 2)[0].Trim();
            if (own.Length > 0 && fullPath is not null && (fullPath == own || fullPath.EndsWith("/" + own.TrimStart('/'), StringComparison.Ordinal)))
                return (i, lines[i].Length - trimmed.Length);
        }
        return (0, 0);
    }

    private static IEnumerable<(int Line, int Col)> Occurrences(string text, string needle)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
            for (var at = lines[i].IndexOf(needle, StringComparison.Ordinal); at >= 0; at = lines[i].IndexOf(needle, at + 1, StringComparison.Ordinal))
                if (at == 0 || !(char.IsLetterOrDigit(lines[i][at - 1]) || lines[i][at - 1] == '_'))
                    yield return (i, at);
    }

    private static SortedSet<string> UnknownFunctions(RuleSet ruleSet)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        void Expr(RuleExpression x)
        {
            switch (x)
            {
                case CallExpression c:
                    if (!RuleFunctions.Names.Contains(c.Name)) names.Add(c.Name);
                    foreach (var a in c.Args) Expr(a);
                    break;
                case MemberExpression m: Expr(m.Target); break;
                case IndexExpression ix: Expr(ix.Target); Expr(ix.Index); break;
                case CompareExpression cmp: Expr(cmp.Left); Expr(cmp.Right); break;
                case AndExpression a: foreach (var o in a.Operands) Expr(o); break;
                case OrExpression o: foreach (var p in o.Operands) Expr(p); break;
                case NotExpression n: Expr(n.Operand); break;
                case InExpression i: Expr(i.Item); Expr(i.Collection); break;
                case ConditionalExpression cond: Expr(cond.Condition); Expr(cond.Then); Expr(cond.Else); break;
            }
        }
        void Block(MatchBlock b)
        {
            foreach (var a in b.Allow) Expr(a.Condition);
            foreach (var m in b.Matches) Block(m);
        }
        foreach (var m in ruleSet.Matches) Block(m);
        return names;
    }

    // ----- JSON-RPC framing --------------------------------------------------------------------

    private async Task<JsonNode?> ReadMessageAsync(CancellationToken ct)
    {
        var length = -1;
        while (true)
        {
            var header = await ReadLineAsync(ct);
            if (header is null) return null;
            if (header.Length == 0) break;
            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(header["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        }
        if (length < 0) return null;
        var buffer = new byte[length];
        await input.ReadExactlyAsync(buffer, ct);
        return JsonNode.Parse(buffer);
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await input.ReadAsync(one, ct) == 0) return bytes.Count == 0 ? null : Encoding.ASCII.GetString([.. bytes]);
            if (one[0] == '\n') return Encoding.ASCII.GetString([.. bytes]).TrimEnd('\r');
            bytes.Add(one[0]);
        }
    }

    private Task RespondAsync(JsonNode? id, JsonNode? result) =>
        SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result });

    private Task RespondErrorAsync(JsonNode id, int code, string message) =>
        SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        });

    private Task NotifyAsync(string method, JsonNode @params) =>
        SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params });

    private Task PublishAsync(string uri, JsonArray diagnostics) =>
        NotifyAsync("textDocument/publishDiagnostics", new JsonObject { ["uri"] = uri, ["diagnostics"] = diagnostics });

    private async Task SendAsync(JsonNode message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _write.WaitAsync();
        try
        {
            await output.WriteAsync(header);
            await output.WriteAsync(body);
            await output.FlushAsync();
        }
        finally
        {
            _write.Release();
        }
    }
}
