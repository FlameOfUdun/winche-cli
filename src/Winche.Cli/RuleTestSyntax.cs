using System.Globalization;
using System.Text;
using Winche.Rules;
using Winche.Rules.Expressions;
using Winche.Values;

namespace Winche.Cli;

/// <summary>A problem in a <c>.rules.test</c> file, at a 1-based line and column.</summary>
internal sealed class RuleTestSyntaxException(string message, int line, int column) : Exception(message)
{
    public int Line { get; } = line;
    public int Column { get; } = column;
}

/// <summary>One <c>allow</c>/<c>deny</c> line and the setup in force where it appears.</summary>
internal sealed record RuleCheck(
    string Test,
    int Line,
    bool ExpectAllow,
    RuleOperation Operation,
    string OperationName,
    string Path,
    Value Auth,                                   // the claims map, or Null when signed out
    Value Resource,                               // the existing document/file, or Null
    Value Incoming,                               // `with { ... }`, or Null
    IReadOnlyList<QueryFilter> Where,
    bool CollectionGroup,
    DateTimeOffset? Time,
    IReadOnlyDictionary<string, Value> Documents)
{
    public string Describe()
    {
        var sb = new StringBuilder($"{(ExpectAllow ? "allow" : "deny")} {OperationName} {(CollectionGroup ? "group " : "")}{Path}");
        if (Incoming is not NullValue) sb.Append(" with {…}");
        if (Where.Count > 0) sb.Append(" where ").Append(string.Join(" && ", Where.Select(w => $"{string.Join('.', w.Field)} {w.OperatorText} …")));
        return sb.ToString();
    }
}

internal sealed record QueryFilter(IReadOnlyList<string> Field, ComparisonOperator Operator, string OperatorText, Value Value);

/// <summary>
/// Parses rule tests, written in the rules language's style:
/// <code>
/// documents {                              // what get() / exists() see, for every test
///   teams/t1: { active: true }
/// }
///
/// test "owners read their profile" {
///   auth { uid: 'u1' }                     // request.auth's claims; omit (or `auth none`) when signed out
///   resource { ownerId: 'u1' }             // the existing document
///   allow get users/u1
///   deny get users/u2
/// }
///
/// test "can only set name" {
///   auth { uid: 'u1' }
///   allow create users/u1 with { name: 'Ann' }
///   deny create users/u1 with { name: 'Ann', admin: true }
/// }
///
/// test "list own notes" {
///   auth { uid: 'u1' }
///   allow list notes where ownerId == 'u1'
///   deny list notes
///   allow list group comments where authorId == 'u1'   // a collection group query
/// }
/// </code>
/// Setup lines (<c>auth</c>, <c>resource</c>, <c>time</c>, <c>documents</c>) apply to the checks after them in
/// the same test. Values: <c>'strings'</c>, numbers, <c>true false null</c>, <c>[lists]</c>,
/// <c>{maps}</c> (keys bare or quoted), <c>timestamp('…')</c>, <c>path('…')</c>, <c>bytes('base64')</c>.
/// </summary>
internal sealed class RuleTestParser
{
    private readonly string _text;
    private int _pos;

    private RuleTestParser(string text) => _text = text;

    private static readonly Dictionary<string, RuleOperation> Ops = new(StringComparer.Ordinal)
    {
        ["get"] = RuleOperation.Get,
        ["list"] = RuleOperation.List,
        ["create"] = RuleOperation.Create,
        ["update"] = RuleOperation.Update,
        ["delete"] = RuleOperation.Delete,
    };

    private static readonly Dictionary<string, ComparisonOperator> Comparisons = new(StringComparer.Ordinal)
    {
        ["=="] = ComparisonOperator.Eq,
        ["!="] = ComparisonOperator.Ne,
        ["<="] = ComparisonOperator.Le,
        [">="] = ComparisonOperator.Ge,
        ["<"] = ComparisonOperator.Lt,
        [">"] = ComparisonOperator.Gt,
    };

    public static IReadOnlyList<RuleCheck> Parse(string text) => new RuleTestParser(text).ParseFile();

    private List<RuleCheck> ParseFile()
    {
        var checks = new List<RuleCheck>();
        var shared = new Dictionary<string, Value>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            Skip();
            if (AtEnd) break;
            var start = _pos;
            var word = Word();
            switch (word)
            {
                case "documents":
                    ParseDocuments(shared);
                    break;
                case "test":
                    Skip();
                    var nameAt = _pos;
                    var name = ParseString();
                    if (!names.Add(name)) Fail($"a test named \"{name}\" already exists", nameAt);
                    ParseTest(name, shared, checks);
                    break;
                default:
                    Fail(word.Length == 0 ? $"unexpected '{_text[start]}'" : $"expected `test \"name\" {{` or `documents {{`, found '{word}'", start);
                    break;
            }
        }
        return checks;
    }

    private void ParseTest(string name, Dictionary<string, Value> shared, List<RuleCheck> checks)
    {
        Expect('{');
        Value auth = RuleValues.Null;
        Value resource = RuleValues.Null;
        DateTimeOffset? time = null;
        var documents = new Dictionary<string, Value>(shared, StringComparer.Ordinal);
        var count = 0;

        while (true)
        {
            Skip();
            if (TryChar('}')) break;
            if (AtEnd) Fail($"test \"{name}\" isn't closed with '}}'", _pos);
            var start = _pos;
            var word = Word();
            switch (word)
            {
                case "auth":
                    auth = MapOrNone("auth");
                    break;
                case "resource":
                    resource = MapOrNone("resource");
                    break;
                case "time":
                    time = ParseTime();
                    break;
                case "documents":
                    ParseDocuments(documents);
                    break;
                case "allow" or "deny":
                    checks.Add(ParseCheck(name, word == "allow", start, auth, resource, time, documents));
                    count++;
                    break;
                default:
                    Fail(word.Length == 0
                        ? $"unexpected '{_text[start]}'"
                        : $"expected allow, deny, auth, resource, time or documents, found '{word}'", start);
                    break;
            }
        }
        if (count == 0) Fail($"test \"{name}\" has no allow or deny line", _pos - 1);
    }

    private RuleCheck ParseCheck(string test, bool allow, int start, Value auth, Value resource,
        DateTimeOffset? time, Dictionary<string, Value> documents)
    {
        Skip();
        var opAt = _pos;
        var opName = Word();
        if (!Ops.TryGetValue(opName, out var op))
            Fail($"expected get, list, create, update or delete after {(allow ? "allow" : "deny")}, found '{opName}'", opAt);

        var group = false;
        Skip();
        var save = _pos;
        if (op == RuleOperation.List && Word() == "group") group = true;
        else _pos = save;

        var path = ParsePath();

        Value incoming = RuleValues.Null;
        var where = new List<QueryFilter>();
        while (true)
        {
            Skip();
            save = _pos;
            var kw = Word();
            if (kw == "with")
            {
                if (op is not (RuleOperation.Create or RuleOperation.Update)) Fail("`with` (the incoming document) is for create and update", save);
                Skip();
                var at = _pos;
                incoming = ParseValue();
                if (incoming is not MapValue) Fail("`with` takes a map: with { field: value }", at);
            }
            else if (kw == "where")
            {
                if (op != RuleOperation.List) Fail("`where` is for list", save);
                where.Add(ParseFilter());
                while (TryOperator("&&")) where.Add(ParseFilter());
            }
            else
            {
                _pos = save;
                break;
            }
        }

        if (op == RuleOperation.Create && incoming is NullValue)
            Fail("a create needs the new document: create <path> with { ... }", start);

        var (line, _) = Position(start);
        return new RuleCheck(test, line, allow, op, opName, path, auth, resource, incoming, where, group, time,
            new Dictionary<string, Value>(documents, StringComparer.Ordinal));
    }

    private QueryFilter ParseFilter()
    {
        Skip();
        var at = _pos;
        var field = new List<string> { Identifier("a field name") };
        while (TryChar('.')) field.Add(Identifier("a field name"));
        Skip();
        var opAt = _pos;
        foreach (var (text, op) in Comparisons)
            if (TryOperator(text))
                return new QueryFilter(field, op, text, ParseValue());
        Fail("expected == != < <= > >=", opAt);
        throw new InvalidOperationException();
    }

    private void ParseDocuments(Dictionary<string, Value> into)
    {
        Expect('{');
        while (true)
        {
            Skip();
            if (TryChar('}')) return;
            if (AtEnd) Fail("documents isn't closed with '}'", _pos);
            var path = ParsePath();
            Expect(':');
            Skip();
            var at = _pos;
            var value = ParseValue();
            if (value is not MapValue) Fail("a document is a map: path: { field: value }", at);
            into[path] = value;
            Skip();
            TryChar(',');
        }
    }

    private Value MapOrNone(string what)
    {
        Skip();
        var at = _pos;
        if (Peek() != '{')
        {
            if (Word() == "none") return RuleValues.Null;
            Fail($"{what} takes a map {{ field: value }} or none", at);
        }
        var map = ParseValue();
        return what == "auth" && map is MapValue { Fields.Count: 0 } ? RuleValues.Null : map;
    }

    private DateTimeOffset ParseTime()
    {
        Skip();
        var at = _pos;
        var v = ParseValue();
        if (v is TimestampValue ts) return ts.Value;
        if (v is StringValue str && DateTimeOffset.TryParse(str.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)) return t;
        Fail("time takes timestamp('2026-01-01T00:00:00Z')", at);
        throw new InvalidOperationException();
    }

    // ----- values ----------------------------------------------------------------------------

    private Value ParseValue()
    {
        Skip();
        var at = _pos;
        var c = Peek();
        switch (c)
        {
            case '\'' or '"':
                return new StringValue(ParseString());
            case '[':
                _pos++;
                var items = new List<Value>();
                Skip();
                if (TryChar(']')) return new ArrayValue(items);
                do { items.Add(ParseValue()); Skip(); } while (TryChar(',') && !PeekIs(']'));
                Expect(']');
                return new ArrayValue(items);
            case '{':
                _pos++;
                var map = new Dictionary<string, Value>(StringComparer.Ordinal);
                while (true)
                {
                    Skip();
                    if (TryChar('}')) return new MapValue(map);
                    var keyAt = _pos;
                    var key = Peek() is '\'' or '"' ? ParseString() : Identifier("a key");
                    if (map.ContainsKey(key)) Fail($"key '{key}' appears twice", keyAt);
                    Expect(':');
                    map[key] = ParseValue();
                    Skip();
                    if (!TryChar(',')) { Expect('}'); return new MapValue(map); }
                }
        }

        if (c == '-' || char.IsAsciiDigit(c)) return ParseNumber();

        var word = Word();
        switch (word)
        {
            case "true": return RuleValues.True;
            case "false": return RuleValues.False;
            case "null": return RuleValues.Null;
            case "timestamp" or "path" or "bytes" or "fileRef" or "userRef":
                Expect('(');
                Skip();
                var argAt = _pos;
                var arg = ParseString();
                Expect(')');
                try
                {
                    return word switch
                    {
                        "timestamp" => new TimestampValue(DateTimeOffset.Parse(arg, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)),
                        "path" => new ReferenceValue(arg.Trim('/')),
                        "fileRef" => new FileRefValue(arg.TrimStart('/')),
                        "userRef" => new UserRefValue(arg),
                        _ => new BytesValue(Convert.FromBase64String(arg)),
                    };
                }
                catch (FormatException)
                {
                    Fail($"'{arg}' isn't a valid {word}", argAt);
                    throw;
                }
        }
        Fail(word.Length == 0 ? "expected a value" : $"expected a value, found '{word}' (strings are quoted: '{word}')", at);
        throw new InvalidOperationException();
    }

    private Value ParseNumber()
    {
        var start = _pos;
        if (Peek() == '-') _pos++;
        while (!AtEnd && (char.IsAsciiDigit(Peek()) || Peek() is '.' or 'e' or 'E' || (Peek() is '+' or '-' && _text[_pos - 1] is 'e' or 'E'))) _pos++;
        var s = _text[start.._pos];
        if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return new IntegerValue(l);
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return new DoubleValue(d);
        Fail($"'{s}' isn't a number", start);
        throw new InvalidOperationException();
    }

    private string ParseString()
    {
        Skip();
        var at = _pos;
        var quote = Peek();
        if (quote is not ('\'' or '"')) Fail("expected a quoted string", at);
        _pos++;
        var sb = new StringBuilder();
        while (true)
        {
            if (AtEnd || Peek() == '\n') Fail("the string isn't closed", at);
            var c = _text[_pos++];
            if (c == quote) return sb.ToString();
            if (c == '\\' && !AtEnd)
            {
                var e = _text[_pos++];
                sb.Append(e switch { 'n' => '\n', 't' => '\t', _ => e });
            }
            else sb.Append(c);
        }
    }

    private string ParsePath()
    {
        Skip();
        var start = _pos;
        while (!AtEnd && (char.IsAsciiLetterOrDigit(Peek()) || Peek() is '/' or '_' or '-' or '.' or '@' or '~' or '%' or '+')) _pos++;
        var path = _text[start.._pos].Trim('/');
        if (path.Length == 0) Fail("expected a path, e.g. users/u1", start);
        if (path.Contains("//", StringComparison.Ordinal)) Fail($"'{path}' has an empty segment", start);
        return path;
    }

    // ----- tokens ----------------------------------------------------------------------------

    private bool AtEnd => _pos >= _text.Length;
    private char Peek() => AtEnd ? '\0' : _text[_pos];
    private bool PeekIs(char c) { Skip(); return Peek() == c; }

    /// <summary>Skips whitespace and <c>//</c> comments.</summary>
    private void Skip()
    {
        while (!AtEnd)
        {
            if (char.IsWhiteSpace(Peek())) _pos++;
            else if (Peek() == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '/')
                while (!AtEnd && Peek() != '\n') _pos++;
            else break;
        }
    }

    private string Word()
    {
        Skip();
        var start = _pos;
        while (!AtEnd && (char.IsAsciiLetterOrDigit(Peek()) || Peek() == '_')) _pos++;
        return _text[start.._pos];
    }

    private string Identifier(string what)
    {
        Skip();
        var at = _pos;
        if (Peek() == '`')
        {
            var end = _text.IndexOf('`', _pos + 1);
            if (end < 0) Fail("the `name` isn't closed", at);
            var name = _text[(_pos + 1)..end];
            _pos = end + 1;
            return name;
        }
        if (!(char.IsAsciiLetter(Peek()) || Peek() == '_')) Fail($"expected {what}", at);
        return Word();
    }

    private bool TryChar(char c)
    {
        Skip();
        if (Peek() != c) return false;
        _pos++;
        return true;
    }

    private bool TryOperator(string op)
    {
        Skip();
        if (string.CompareOrdinal(_text, _pos, op, 0, op.Length) != 0) return false;
        _pos += op.Length;
        return true;
    }

    private void Expect(char c)
    {
        if (!TryChar(c)) Fail(AtEnd ? $"expected '{c}', found the end of the file" : $"expected '{c}', found '{Peek()}'", _pos);
    }

    private (int Line, int Column) Position(int offset)
    {
        int line = 1, col = 1;
        for (var i = 0; i < offset && i < _text.Length; i++)
        {
            if (_text[i] == '\n') { line++; col = 1; }
            else col++;
        }
        return (line, col);
    }

    private void Fail(string message, int offset)
    {
        var (line, col) = Position(offset);
        throw new RuleTestSyntaxException(message, line, col);
    }
}
