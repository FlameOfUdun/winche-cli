# Winche CLI

[![NuGet](https://img.shields.io/nuget/v/Winche.Cli.svg)](https://www.nuget.org/packages/Winche.Cli)

`winche` is the command-line tool for [Winche](https://github.com/FlameOfUdun) apps. You keep access rules in
your repo as `.rules` files, check them offline, and deploy them to the app's
[Winche console](https://github.com/FlameOfUdun/winche-console), like `firebase deploy`. Each deploy is a new
version in the console's rules history, with the git commit as its note, so it can be reviewed and reverted
there.

```sh
dotnet tool install -g Winche.Cli
```

## Commands

```sh
winche init --console https://api.example.com/_console   # winche.json, rules files and their test files
winche use --add prod https://api.example.com/_console   # name a console (and use it here)
winche use dev                                           # switch projects; `winche use` lists them
winche login                                             # browser sign-in (Keycloak)
winche rules validate                                    # offline: syntax, paths, unknown functions
winche rules test                                        # offline: run the allow/deny cases
winche rules diff                                        # local files vs. what the console enforces
winche rules deploy                                      # run the tests, then publish changed files as new versions
winche rules deploy --dry-run                            # show the changes without saving
winche rules history                                     # the console's versions (who, when, note)
winche rules history 3 --only database                   # version 3 against the active one
winche rules rollback 3 --only database                  # make version 3 active again
winche rules pull                                        # write the live rules into the files
winche logout
```

Options: `-P/--project <alias>`, `--console <url>`, `--token <jwt>`, `--config <path>`, `--only database|storage`,
`-m/--message <note>` (deploy), `--force` (deploy unchanged files too), `--skip-tests` (deploy), `-y/--yes`
(rollback without a prompt), `--stdout` (pull), `--port <n>` (login).
Run `winche --help` for the full list.

## `winche.json`

Found in the working directory or the nearest parent; rule file paths are relative to it.

```json
{
  "console": "https://api.example.com/_console",
  "rules": { "database": "database.rules", "storage": "storage.rules" },
  "tests": { "database": "database.rules.test", "storage": "storage.rules.test" }
}
```

The rules language is Winche.Rules' (Firestore-style) — see the **[Rules Language Guide](https://github.com/FlameOfUdun/winche-rules/blob/main/GUIDE.md)**:

```text
match users/{userId} {
  allow read, write: if request.auth.uid == userId;
}
```

## Projects

Name your consoles in `winche.json` and pick one per checkout, like `firebase use`:

```json
{
  "projects": { "dev": "http://localhost:5000/_console", "prod": "https://api.example.com/_console" },
  "rules": { "database": "database.rules" }
}
```

`winche use <alias>` picks the project for this checkout (kept on your machine, not in the repo, so each person
and CI job picks its own); `winche use` lists them (`*` is the picked one); `winche use --add <alias> <url>`,
`--remove <alias>` and `--clear` edit them. Every command that talks to a console prints `Project: prod (…)` first.
Which console a command uses: `--console`, else `-P/--project`, else `WINCHE_CONSOLE`, else the picked project,
else the only project, else `"console"`. Sign-ins are kept per console, so switching doesn't sign you out.

## Testing rules

`winche rules test` runs checks against the local rules files, offline, so CI can catch a rule mistake before
it ships (`winche rules deploy` runs them first and stops on a failure). Tests are written in the rules
language's style, in `database.rules.test` / `storage.rules.test`:

```text
documents {                                  // what get() / exists() see, in every test
  teams/t1: { active: true }
}

test "owners read their profile" {
  auth { uid: 'u1' }                         // request.auth's claims; omit (or `auth none`) when signed out
  resource { ownerId: 'u1' }                 // the existing document
  allow get users/u1
  deny get users/u2
}

test "can only set name" {
  auth { uid: 'u1' }
  allow create users/u1 with { name: 'Ann' }
  deny create users/u1 with { name: 'Ann', admin: true }
}

test "lists are proved against their filters" {
  auth { uid: 'u1' }
  allow list notes where ownerId == 'u1'
  deny list notes
  allow list group comments where authorId == 'u1'      // a collection group query
}

test "only until the cutoff" {
  time timestamp('2027-01-01T00:00:00Z')
  deny get events/e1
}
```

- **Checks:** `allow` or `deny`, then `get list create update delete` and a path. `create`/`update` take the
  incoming document with `with { ... }`; database `list` takes `where field op value` (`== != < <= > >=`,
  joined with `&&`) and `group` for a collection group query.
- **Setup:** `auth`, `resource`, `time` and `documents` apply to the checks after them in the same test.
- **Values:** `'strings'`, numbers, `true false null`, `[lists]`, `{maps}` (keys bare or quoted),
  `timestamp('…')`, `path('…')`, `bytes('base64')`. `//` comments.
- **Like the server:** `auth` becomes `request.auth` (`uid` plus every claim under `token`); a database
  document gets its fields plus `data`, `id`, `path` and `collection`; a database `list` is proved against its
  filters (rules are not filters); values compare exactly as the database compares them.

A failure names the line: `database.rules.test:8: allow delete userData/u1 — got deny`. Syntax errors are
`file:line:column`.

## History and rollback

`winche rules history` lists each subsystem's versions with who saved them, when and the note (`*` marks the
active one); `winche rules history <v>` shows version `<v>` as a diff against the active one. `winche rules
rollback <v>` shows that diff, asks, and makes `<v>` active again as a new version, exactly like the console's
Revert, so the history stays complete (`--yes` skips the prompt for scripts). With both subsystems configured,
pick one with `--only`.

## How deploy works

- **All or nothing on validity.** Every file is checked first and errors are reported as `file:line:column`;
  nothing is published if any file has one, or if a test fails (`--skip-tests` to deploy anyway).
- **Unchanged files are skipped** (`database: up to date (v3)`); `--force` saves them anyway.
- **No lost edits.** A deploy names the version it replaces, so if someone saved in the console meanwhile the
  deploy stops and asks you to run it again.
- **Live straight away.** The console hot-swaps the new rules on every API instance; on restart the deployed
  version replaces the app's starting rules (`UseRulesFile`).

## Signing in

`winche login` signs in with the console's own Keycloak client (authorization code + PKCE with a loopback
redirect, RFC 8252) and saves the tokens in `~/.config/winche/credentials.json`, readable by you only. They
are refreshed as needed.

- **Register the redirect URI** `http://127.0.0.1:9005/callback` on the console's Keycloak client (or use
  `--port <n>` and register that port).
- The account needs the console's **Admin** role.
- **CI:** pass a bearer token for the console client with `--token` or `WINCHE_TOKEN`, and the console with
  `--console` or `WINCHE_CONSOLE`.

## Editors

`winche lsp` is a Language Server Protocol server over stdin/stdout for `.rules` and `.rules.test` files:
errors and warnings as you type (the same checks as `rules validate`), test results on each `test` block and
on failing lines (re-run as you edit either file), formatting (left alone when the file has comments, which
the console doesn't keep), and the document fields the console samples for a match pattern (`winche/fields`,
when signed in). The [Winche VS Code extension](https://github.com/FlameOfUdun/winche-vscode) starts it.

## Requirements

- A host app running Winche.Console 4.0.0+ in **Keycloak mode** with the rules editor enabled
  (`o.AddDatabaseTab(b => b.UseRulesEditor())`, `o.AddStorageTab(b => b.UseRulesEditor())`). Identity-mode
  consoles sign in with cookies, which the CLI doesn't use.
- .NET 10 SDK.

The CLI talks to the console's `api/rules` HTTP API; it doesn't depend on the console package.

## Develop

```sh
dotnet build Winche.Cli.slnx
dotnet test Winche.Cli.slnx      # needs Docker (Testcontainers Postgres)
```

The tests run the CLI in-process against `tests/Winche.Cli.TestHost`, an app with the console in Keycloak mode,
including a full browser sign-in against a fake Keycloak.

Releases: push a `v*` tag; GitHub Actions packs and publishes to NuGet.

## License

See [LICENSE](LICENSE).
