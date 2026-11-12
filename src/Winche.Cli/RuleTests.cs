using System.Text.Json;
using Winche.Database.Authorization;
using Winche.Rules;
using Winche.Rules.Evaluation;
using Winche.Rules.Expressions;
using Winche.Rules.Json;
using Winche.Rules.Querying;

namespace Winche.Cli;

/// <summary>
/// <c>winche rules test</c>: allow/deny cases for a rules file, evaluated offline the way the server builds
/// rule requests (Winche.Database: claims → <c>request.auth {uid, token}</c>, a document's fields plus
/// <c>data</c> / <c>id</c> / <c>path</c> / <c>collection</c> / … as <c>resource</c>, and list queries proved
/// against their <c>where</c> filters; Winche.Storage: file fields as given, list on the directory).
/// <code>
/// {
///   "documents": { "teams/t1": { "active": true } },          // for get() / exists(), shared by every case
///   "cases": [
///     { "name": "owner reads their profile", "expect": "allow",
///       "op": "get", "path": "users/u1", "auth": { "uid": "u1" }, "resource": { "name": "Ann" } },
///     { "name": "signed out can't", "expect": "deny", "op": "get", "path": "users/u1" },
///     { "name": "list own notes", "expect": "allow", "op": "list", "path": "notes",
///       "auth": { "uid": "u1" }, "where": [["ownerId", "==", "u1"]] }
///   ]
/// }
/// </code>
/// Values are JSON; timestamps, bytes, paths and exact integers use the tagged forms
/// <c>{"$timestamp": "2026-01-01T00:00:00Z"}</c>, <c>{"$bytes": "…"}</c>, <c>{"$path": "…"}</c>, <c>{"$integer": "5"}</c>.
/// </summary>
internal static class RuleTests
{
    internal sealed record Outcome(string Name, bool Passed, string? Detail);

    private static readonly Dictionary<string, RuleOperation> Ops = new(StringComparer.Ordinal)
    {
        ["get"] = RuleOperation.Get,
        ["list"] = RuleOperation.List,
        ["create"] = RuleOperation.Create,
        ["update"] = RuleOperation.Update,
        ["delete"] = RuleOperation.Delete,
    };

    private static readonly Dictionary<string, ComparisonOperator> Operators = new(StringComparer.Ordinal)
    {
        ["=="] = ComparisonOperator.Eq,
        ["!="] = ComparisonOperator.Ne,
        ["<"] = ComparisonOperator.Lt,
        ["<="] = ComparisonOperator.Le,
        [">"] = ComparisonOperator.Gt,
        [">="] = ComparisonOperator.Ge,
    };

    /// <summary>Runs every case in <paramref name="testFile"/> against <paramref name="ruleSet"/>.</summary>
    /// <exception cref="CliException">The test file itself is malformed.</exception>
    public static async Task<IReadOnlyList<Outcome>> RunAsync(string subsystem, RuleSet ruleSet, string testFile, CancellationToken ct)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(testFile), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new CliException($"{testFile}: invalid JSON ({ex.Message}).");
        }

        if (!root.TryGetProperty("cases", out var cases) || cases.ValueKind != JsonValueKind.Array)
            throw new CliException($"{testFile}: expected {{ \"cases\": [ ... ] }}.");

        var database = subsystem == "database";
        var shared = Documents(root, database, testFile, "documents");
        var comparer = database ? (IRuleValueComparer)WincheRuleValueComparer.Instance : new DefaultRuleValueComparer();
        var engine = new RuleEngine(new StaticRuleSetRepository(ruleSet), comparer);

        var outcomes = new List<Outcome>();
        var index = 0;
        foreach (var c in cases.EnumerateArray())
        {
            index++;
            var name = c.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : $"case {index}";
            try
            {
                outcomes.Add(await RunCaseAsync(engine, c, name, database, shared, ct));
            }
            catch (CaseException ex)
            {
                outcomes.Add(new Outcome(name, false, ex.Message));
            }
        }
        return outcomes;
    }

    private sealed class CaseException(string message) : Exception(message);

    private static async Task<Outcome> RunCaseAsync(
        RuleEngine engine, JsonElement c, string name, bool database,
        Dictionary<string, RuleValue> shared, CancellationToken ct)
    {
        var expect = String(c, "expect") ?? throw new CaseException("missing \"expect\" (allow or deny)");
        if (expect is not ("allow" or "deny")) throw new CaseException($"\"expect\": \"{expect}\" — use allow or deny");
        var opName = String(c, "op") ?? throw new CaseException("missing \"op\" (get, list, create, update, delete)");
        if (!Ops.TryGetValue(opName, out var op)) throw new CaseException($"\"op\": \"{opName}\" — use get, list, create, update or delete");
        var path = (String(c, "path") ?? throw new CaseException("missing \"path\"")).Trim('/');

        var time = c.TryGetProperty("time", out var t)
            ? (DateTimeOffset.TryParse(t.GetString(), out var parsed) ? parsed : throw new CaseException("\"time\" isn't an ISO date"))
            : DateTimeOffset.UtcNow;

        var documents = new Dictionary<string, RuleValue>(shared, StringComparer.Ordinal);
        foreach (var (k, v) in Documents(c, database, null, "documents")) documents[k] = v;

        var resource = c.TryGetProperty("resource", out var r) ? Resource(Value(r, "resource"), path, database, time) : RuleValue.Null;
        var incoming = c.TryGetProperty("request", out var q) ? Resource(Value(q, "request"), path, database, time) : RuleValue.Null;
        if (op is RuleOperation.Create && incoming.Kind == RuleValueKind.Null)
            throw new CaseException("a create needs \"request\" (the new document)");

        var request = RuleValue.Map(new Dictionary<string, RuleValue>
        {
            ["auth"] = Auth(c),
            ["resource"] = incoming,
            ["method"] = RuleValue.String(opName),
            ["time"] = RuleValue.Timestamp(time),
        });
        var provider = new MapProvider(documents);

        bool allowed;
        if (op == RuleOperation.List && database)
        {
            var group = c.TryGetProperty("collectionGroup", out var g) && g.ValueKind == JsonValueKind.True;
            allowed = engine.Allows(new QueryConstraints(path, Where(c), group), new RuleRequest { Request = request, Provider = provider });
        }
        else
        {
            allowed = await engine.AllowsAsync(op, path, new RuleRequest { Resource = resource, Request = request, Provider = provider }, ct);
        }

        var passed = allowed == (expect == "allow");
        return new Outcome(name, passed, passed ? null : $"expected {expect}, got {(allowed ? "allow" : "deny")}");
    }

    private static string? String(JsonElement c, string property) =>
        c.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static RuleValue Value(JsonElement element, string what)
    {
        try
        {
            return JsonSerializer.Deserialize<RuleValue>(element.GetRawText(), RuleJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            throw new CaseException($"\"{what}\": {ex.Message}");
        }
    }

    /// <summary><c>request.auth</c>: null when signed out, else <c>{uid, token: every claim}</c>.</summary>
    private static RuleValue Auth(JsonElement c)
    {
        if (!c.TryGetProperty("auth", out var a) || a.ValueKind == JsonValueKind.Null) return RuleValue.Null;
        if (a.ValueKind != JsonValueKind.Object) throw new CaseException("\"auth\" must be an object of claims (or null)");
        var claims = Value(a, "auth");
        if (claims.AsMap.Count == 0) return RuleValue.Null;
        var uid = claims.AsMap.TryGetValue("uid", out var u) && u.Kind != RuleValueKind.Null
            ? u.Kind switch
            {
                RuleValueKind.String => u,
                RuleValueKind.Int => RuleValue.String(u.AsInt.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                _ => throw new CaseException("\"auth.uid\" must be a string"),
            }
            : RuleValue.Null;
        return RuleValue.Map(new Dictionary<string, RuleValue> { ["uid"] = uid, ["token"] = claims });
    }

    /// <summary>A document as Winche.Database exposes it to rules (fields + data + storage columns); files as given.</summary>
    private static RuleValue Resource(RuleValue fields, string path, bool database, DateTimeOffset time)
    {
        if (!database || fields.Kind != RuleValueKind.Map) return fields;
        var map = new Dictionary<string, RuleValue>(fields.AsMap, StringComparer.Ordinal);
        var data = RuleValue.Map(new Dictionary<string, RuleValue>(map, StringComparer.Ordinal));
        var segments = path.Split('/');
        map["id"] = RuleValue.String(segments[^1]);
        map["path"] = RuleValue.Path(path);
        map["collection"] = RuleValue.String(segments.Length > 1 ? segments[^2] : "");
        map.TryAdd("createdAt", RuleValue.Timestamp(time));
        map.TryAdd("updatedAt", RuleValue.Timestamp(time));
        map.TryAdd("version", RuleValue.Int(1));
        map.TryAdd("data", data);
        return RuleValue.Map(map);
    }

    private static Dictionary<string, RuleValue> Documents(JsonElement owner, bool database, string? file, string property)
    {
        var result = new Dictionary<string, RuleValue>(StringComparer.Ordinal);
        if (!owner.TryGetProperty(property, out var docs)) return result;
        if (docs.ValueKind != JsonValueKind.Object)
        {
            var message = $"\"{property}\" must map paths to documents";
            throw file is null ? new CaseException(message) : new CliException($"{file}: {message}.");
        }
        foreach (var d in docs.EnumerateObject())
        {
            var path = d.Name.Trim('/');
            result[path] = Resource(Value(d.Value, $"{property}.{d.Name}"), path, database, DateTimeOffset.UtcNow);
        }
        return result;
    }

    private static List<QueryConstraint> Where(JsonElement c)
    {
        var list = new List<QueryConstraint>();
        if (!c.TryGetProperty("where", out var where)) return list;
        foreach (var w in where.EnumerateArray())
        {
            if (w.ValueKind != JsonValueKind.Array || w.GetArrayLength() != 3 || w[0].ValueKind != JsonValueKind.String || w[1].ValueKind != JsonValueKind.String)
                throw new CaseException("each \"where\" is [\"field\", \"==\", value]");
            var opText = w[1].GetString()!;
            if (!Operators.TryGetValue(opText, out var op))
                throw new CaseException($"\"where\" operator '{opText}' — use == != < <= > >=");
            list.Add(new QueryConstraint(w[0].GetString()!.Split('.'), op, Value(w[2], "where")));
        }
        return list;
    }

    /// <summary>get() / exists() over the case's documents.</summary>
    private sealed class MapProvider(Dictionary<string, RuleValue> documents) : IRuleResourceProvider
    {
        public Task<bool> ExistsAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(documents.ContainsKey(path.Trim('/')));

        public Task<RuleValue> GetAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(documents.GetValueOrDefault(path.Trim('/'), RuleValue.Null));
    }
}
