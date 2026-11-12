using Xunit;

namespace Winche.Cli.Tests;

/// <summary><c>winche rules test</c>: offline, so no console is involved.</summary>
public sealed class RuleTestsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("winche-cli-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Write(string file, string text) => File.WriteAllText(Path.Combine(_dir, file), text);

    private async Task<(int Exit, string Out, string Err)> Test(string rules, string tests, string subsystem = "database")
    {
        Write("winche.json", $$"""{ "rules": { "{{subsystem}}": "r.rules" }, "tests": { "{{subsystem}}": "r.rules.test" } }""");
        Write("r.rules", rules);
        Write("r.rules.test", tests);
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
          allow get: if request.time < timestamp('2027-01-01T00:00:00Z') && request.auth.token.role in ['admin', 'staff'];
        }
        match {path=**}/comments/{id} {
          allow list: if resource.authorId == request.auth.uid;
        }
        """;

    [Fact]
    public async Task Checks_are_evaluated_like_the_database()
    {
        var (exit, output, err) = await Test(Rules, """
            // shared by every test
            documents {
              teams/t1: { active: true }
            }

            test "users read their own" {
              auth { uid: 'u1' }
              allow get users/u1
              deny get users/u2
            }

            test "signed out" {
              deny get users/u1
              auth none
              deny get users/u1
            }

            test "create sets only name" {
              auth { uid: 'u1' }
              allow create users/u1 with { name: 'Ann' }
              deny create users/u1 with { name: 'Ann', admin: true }
            }

            test "update changes only name" {
              auth { uid: 'u1' }
              resource { ownerId: 'u1', name: 'A' }
              allow update users/u1 with { ownerId: 'u1', name: 'B' }
              deny update users/u1 with { ownerId: 'u2', name: 'A' }
            }

            test "lists are proved against their filters" {
              auth { uid: 'u1' }
              allow list notes where ownerId == 'u1'
              deny list notes
              deny list notes where ownerId == 'u2'
              allow list group comments where authorId == 'u1'
            }

            test "get() and exists()" {
              allow get notes/n1
              documents { teams/t1: { active: false } }    // this test's own documents win
              deny get notes/n1
            }

            test "time and claims" {
              auth { uid: 'u1', role: 'staff' }
              time timestamp('2026-06-01T00:00:00Z')
              allow get events/e1
              time timestamp('2027-06-01T00:00:00Z')
              deny get events/e1
              auth { uid: 'u1', role: "guest", tags: ['a', 1, 2.5, true, null], `odd key`: { n: -3 } }
              time timestamp('2026-06-01T00:00:00Z')
              deny get events/e1
            }
            """);

        Assert.True(exit == 0, output + err);
        Assert.Contains("17 checks passed", output);
        Assert.Contains("✓ lists are proved against their filters", output);
    }

    [Fact]
    public async Task Failures_show_the_line_and_exit_1()
    {
        var (exit, output, _) = await Test(Rules, """
            test "wrong expectation" {
              auth { uid: 'u2' }
              allow get users/u2
              allow get users/u1
            }
            """);

        Assert.Equal(1, exit);
        Assert.Contains("✗ wrong expectation", output);
        Assert.Contains("r.rules.test:4: allow get users/u1 — got deny", output);
        Assert.Contains("1 checks passed, 1 failed", output);
    }

    [Theory]
    [InlineData("test \"a\" {\n  allow read users/u1\n}", "r.rules.test:2:9: expected get, list, create, update or delete")]
    [InlineData("test \"a\" {\n  allow create users/u1\n}", "r.rules.test:2:3: a create needs the new document")]
    [InlineData("test \"a\" {\n  auth { uid: u1 }\n  deny get users/u1\n}", "r.rules.test:2:15: expected a value, found 'u1' (strings are quoted: 'u1')")]
    [InlineData("test \"a\" {\n  deny get users/u1 where x == 1\n}", "r.rules.test:2:21: `where` is for list")]
    [InlineData("test \"a\" {\n}", "has no allow or deny line")]
    [InlineData("test \"a\" { deny get a/b }\ntest \"a\" { deny get a/b }", "r.rules.test:2:6: a test named \"a\" already exists")]
    [InlineData("test \"a\" {\n  deny get a/b", "isn't closed")]
    [InlineData("check \"a\" {}", "r.rules.test:1:1: expected `test \"name\" {` or `documents {`")]
    public async Task Syntax_errors_name_file_line_and_column(string tests, string expected)
    {
        var (exit, _, err) = await Test(Rules, tests);
        Assert.Equal(1, exit);
        Assert.Contains(expected, err);
    }

    [Fact]
    public async Task Storage_list_is_checked_on_the_directory()
    {
        var (exit, output, _) = await Test("""
            match userFiles/{uid} {
              allow list: if request.auth.uid == uid;
            }
            """, """
            test "folders" {
              auth { uid: 'u1' }
              allow list userFiles/u1
              deny list userFiles/u2
            }
            """, subsystem: "storage");

        Assert.True(exit == 0, output);
        Assert.Contains("2 checks passed", output);
    }
}
