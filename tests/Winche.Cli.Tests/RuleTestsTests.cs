using Xunit;

namespace Winche.Cli.Tests;

/// <summary><c>winche rules test</c>: offline, so no console is involved.</summary>
public sealed class RuleTestsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("winche-cli-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Write(string file, string text) => File.WriteAllText(Path.Combine(_dir, file), text);

    private async Task<(int Exit, string Out, string Err)> Test(string rules, string cases, string subsystem = "database")
    {
        Write("winche.json", $$"""{ "rules": { "{{subsystem}}": "r.rules" }, "tests": { "{{subsystem}}": "t.json" } }""");
        Write("r.rules", rules);
        Write("t.json", cases);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await CliApp.RunAsync(["rules", "test"], new CliEnvironment
        {
            Out = stdout,
            Error = stderr,
            WorkingDirectory = _dir,
            ConfigDirectory = _dir,
            GetVariable = _ => null,
            Http = new HttpClientHandler(),
            OpenBrowser = _ => { },
        });
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private const string Rules = """
        match users/{uid} {
          allow get: if request.auth.uid == uid;
          allow create: if request.auth.uid == uid && request.resource.data.keys().hasOnly(['name']);
          allow update: if resource.ownerId == request.auth.uid
            && request.resource.data.diff(resource.data).affectedKeys().hasOnly(['name']);
        }
        match notes/{id} {
          allow list: if resource.ownerId == request.auth.uid;
          allow get: if exists('teams/t1') && get('teams/t1').active == true;
        }
        match events/{id} {
          allow get: if request.time < timestamp('2027-01-01T00:00:00Z');
        }
        """;

    [Fact]
    public async Task Cases_are_evaluated_like_the_database()
    {
        var (exit, output, _) = await Test(Rules, """
            {
              // comments are allowed
              "documents": { "teams/t1": { "active": true } },
              "cases": [
                { "name": "get own", "expect": "allow", "op": "get", "path": "users/u1", "auth": { "uid": "u1" } },
                { "name": "get other", "expect": "deny", "op": "get", "path": "users/u1", "auth": { "uid": "u2" } },
                { "name": "signed out", "expect": "deny", "op": "get", "path": "users/u1" },
                { "name": "create with only name", "expect": "allow", "op": "create", "path": "users/u1",
                  "auth": { "uid": "u1" }, "request": { "name": "Ann" } },
                { "name": "create with extra field", "expect": "deny", "op": "create", "path": "users/u1",
                  "auth": { "uid": "u1" }, "request": { "name": "Ann", "admin": true } },
                { "name": "update name only", "expect": "allow", "op": "update", "path": "users/u1", "auth": { "uid": "u1" },
                  "resource": { "ownerId": "u1", "name": "A" }, "request": { "ownerId": "u1", "name": "B" } },
                { "name": "update owner", "expect": "deny", "op": "update", "path": "users/u1", "auth": { "uid": "u1" },
                  "resource": { "ownerId": "u1", "name": "A" }, "request": { "ownerId": "u2", "name": "A" } },
                { "name": "list filtered by owner", "expect": "allow", "op": "list", "path": "notes",
                  "auth": { "uid": "u1" }, "where": [["ownerId", "==", "u1"]] },
                { "name": "list unfiltered", "expect": "deny", "op": "list", "path": "notes", "auth": { "uid": "u1" } },
                { "name": "active team (shared documents)", "expect": "allow", "op": "get", "path": "notes/n1" },
                { "name": "inactive team (case documents win)", "expect": "deny", "op": "get", "path": "notes/n1",
                  "documents": { "teams/t1": { "active": false } } },
                { "name": "token claims", "expect": "allow", "op": "get", "path": "users/u1", "auth": { "uid": "u1", "role": "x" } },
                { "name": "before cutoff", "expect": "allow", "op": "get", "path": "events/e1", "time": "2026-06-01T00:00:00Z" },
                { "name": "after cutoff", "expect": "deny", "op": "get", "path": "events/e1", "time": "2027-06-01T00:00:00Z" }
              ]
            }
            """);

        Assert.True(exit == 0, output);
        Assert.Contains("14 passed", output);
    }

    [Fact]
    public async Task Failures_and_bad_cases_are_reported_and_exit_1()
    {
        var (exit, output, _) = await Test(Rules, """
            { "cases": [
              { "name": "wrong expectation", "expect": "allow", "op": "get", "path": "users/u1", "auth": { "uid": "u2" } },
              { "name": "bad op", "expect": "allow", "op": "read", "path": "users/u1" },
              { "name": "create without request", "expect": "deny", "op": "create", "path": "users/u1" },
              { "expect": "maybe", "op": "get", "path": "users/u1" }
            ] }
            """);

        Assert.Equal(1, exit);
        Assert.Contains("✗ wrong expectation — expected allow, got deny", output);
        Assert.Contains("✗ bad op — \"op\": \"read\"", output);
        Assert.Contains("✗ create without request — a create needs \"request\"", output);
        Assert.Contains("✗ case 4 — \"expect\": \"maybe\"", output);
        Assert.Contains("0 passed, 4 failed", output);
    }

    [Fact]
    public async Task Storage_list_is_checked_on_the_directory()
    {
        var (exit, output, _) = await Test("""
            match userFiles/{uid} {
              allow list: if request.auth.uid == uid;
            }
            """, """
            { "cases": [
              { "name": "own folder", "expect": "allow", "op": "list", "path": "userFiles/u1", "auth": { "uid": "u1" } },
              { "name": "other folder", "expect": "deny", "op": "list", "path": "userFiles/u1", "auth": { "uid": "u2" } }
            ] }
            """, subsystem: "storage");

        Assert.True(exit == 0, output);
        Assert.Contains("2 passed", output);
    }

    [Fact]
    public async Task Malformed_test_file_is_an_error()
    {
        var (exit, _, err) = await Test(Rules, "{ \"cases\": 3 }");
        Assert.Equal(1, exit);
        Assert.Contains("expected { \"cases\": [ ... ] }", err);
    }
}
