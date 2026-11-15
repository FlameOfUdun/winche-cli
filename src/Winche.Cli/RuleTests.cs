using Winche.Database.Authorization;
using Winche.Rules;
using Winche.Rules.Evaluation;
using Winche.Rules.Querying;
using Winche.Values;

namespace Winche.Cli;

/// <summary>
/// <c>winche rules test</c>: runs the checks in a <c>.rules.test</c> file (see <see cref="RuleTestParser"/>)
/// offline, building each request the way the server does (Winche.Database: claims → <c>request.auth
/// {uid, token}</c>, a document's fields plus <c>data</c> / <c>id</c> / <c>path</c> / <c>collection</c> / … as
/// <c>resource</c>, list queries proved against their <c>where</c> filters, the database's value comparer;
/// Winche.Storage: file fields as given, list on the directory).
/// </summary>
internal static class RuleTests
{
    internal sealed record Outcome(RuleCheck Check, bool Allowed)
    {
        public bool Passed => Allowed == Check.ExpectAllow;
    }

    /// <summary>Parses <paramref name="testFile"/> and runs every check against <paramref name="ruleSet"/>.</summary>
    /// <exception cref="RuleTestSyntaxException">The test file doesn't parse.</exception>
    public static Task<IReadOnlyList<Outcome>> RunAsync(string subsystem, RuleSet ruleSet, string testFile, CancellationToken ct) =>
        RunTextAsync(subsystem, ruleSet, File.ReadAllText(testFile), ct);

    /// <summary>Runs every check in <paramref name="tests"/> (a test file's text).</summary>
    public static async Task<IReadOnlyList<Outcome>> RunTextAsync(string subsystem, RuleSet ruleSet, string tests, CancellationToken ct)
    {
        var checks = RuleTestParser.Parse(tests);
        var database = subsystem == "database";
        var engine = new RuleEngine(new StaticRuleSetRepository(ruleSet));

        var outcomes = new List<Outcome>();
        foreach (var check in checks)
            outcomes.Add(new Outcome(check, await AllowsAsync(engine, check, database, ct)));
        return outcomes;
    }

    private static async Task<bool> AllowsAsync(RuleEngine engine, RuleCheck c, bool database, CancellationToken ct)
    {
        var time = c.Time ?? DateTimeOffset.UtcNow;
        var documents = c.Documents.ToDictionary(kv => kv.Key, kv => Resource(kv.Value, kv.Key, database, time), StringComparer.Ordinal);
        var request = new MapValue(new Dictionary<string, Value>
        {
            ["auth"] = Auth(c.Auth),
            ["resource"] = Resource(c.Incoming, c.Path, database, time),
            ["method"] = new StringValue(c.OperationName),
            ["time"] = new TimestampValue(time),
        });
        var provider = new MapProvider(documents);

        if (c.Operation == RuleOperation.List && database)
        {
            var constraints = c.Where.Select(w => new QueryConstraint(w.Field, w.Operator, w.Value)).ToList();
            return engine.Allows(new QueryConstraints(c.Path, constraints, c.CollectionGroup), new RuleRequest { Request = request, Provider = provider });
        }

        return await engine.AllowsAsync(c.Operation, c.Path, new RuleRequest
        {
            Resource = Resource(c.Resource, c.Path, database, time),
            Request = request,
            Provider = provider,
        }, ct);
    }

    /// <summary><c>request.auth</c>: null when signed out, else <c>{uid, token: every claim}</c>.</summary>
    private static Value Auth(Value claims)
    {
        if (claims is not MapValue { Fields.Count: > 0 } map) return RuleValues.Null;
        var uid = map.Fields.GetValueOrDefault("uid") switch
        {
            null or NullValue => RuleValues.Null,
            IntegerValue i => new StringValue(i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            var u => u,
        };
        return new MapValue(new Dictionary<string, Value> { ["uid"] = uid, ["token"] = claims });
    }

    /// <summary>A document as Winche.Database exposes it to rules (fields + data + storage columns); files as given.</summary>
    private static Value Resource(Value fields, string path, bool database, DateTimeOffset time)
    {
        if (!database || fields is not MapValue given) return fields;
        var map = new Dictionary<string, Value>(given.Fields, StringComparer.Ordinal);
        var data = given;
        var segments = path.Split('/');
        map["id"] = new StringValue(segments[^1]);
        map["path"] = new ReferenceValue(path);
        map["collection"] = new StringValue(segments.Length > 1 ? segments[^2] : "");
        map.TryAdd("createdAt", new TimestampValue(time));
        map.TryAdd("updatedAt", new TimestampValue(time));
        map.TryAdd("version", new IntegerValue(1));
        map.TryAdd("data", data);
        return new MapValue(map);
    }

    /// <summary>get() / exists() over the test's documents.</summary>
    private sealed class MapProvider(Dictionary<string, Value> documents) : IRuleResourceProvider
    {
        public Task<bool> ExistsAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(documents.ContainsKey(path.Trim('/')));

        public Task<Value> GetAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(documents.GetValueOrDefault(path.Trim('/'), RuleValues.Null));
    }
}
