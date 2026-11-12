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

Options: `--console <url>`, `--token <jwt>`, `--config <path>`, `--only database|storage`,
`-m/--message <note>` (deploy), `--force` (deploy unchanged files too), `--skip-tests` (deploy), `-y/--yes`
(rollback without a prompt), `--stdout` (pull), `--port <n>` (login).
Run `winche --help` for the full list.

## `winche.json`

Found in the working directory or the nearest parent; rule file paths are relative to it.

```json
{
  "console": "https://api.example.com/_console",
  "rules": { "database": "database.rules", "storage": "storage.rules" },
  "tests": { "database": "database.rules.test.json", "storage": "storage.rules.test.json" }
}
```

The rules language is Winche.Rules' (Firestore-style):

```text
match users/{userId} {
  allow read, write: if request.auth.uid == userId;
}
```

## Testing rules

`winche rules test` runs allow/deny cases against the local rules files, offline, so CI can catch a rule
mistake before it ships (`winche rules deploy` runs them first and stops on a failure). Each case is evaluated
the way the server builds the request: `auth` becomes `request.auth` (`uid` plus every claim under `token`), a
database `resource` / `request` gets its fields plus `data`, `id`, `path` and `collection`, and a database
`list` is proved against its `where` filters (rules are not filters). Values compare exactly as the database
compares them.

```jsonc
{
  "documents": { "teams/t1": { "active": true } },        // what get() / exists() see
  "cases": [
    { "name": "owner reads their profile", "expect": "allow",
      "op": "get", "path": "users/u1", "auth": { "uid": "u1" }, "resource": { "name": "Ann" } },
    { "name": "signed out can't", "expect": "deny", "op": "get", "path": "users/u1" },
    { "name": "can only set name", "expect": "deny", "op": "create", "path": "users/u1",
      "auth": { "uid": "u1" }, "request": { "name": "Ann", "admin": true } },
    { "name": "list own notes", "expect": "allow", "op": "list", "path": "notes",
      "auth": { "uid": "u1" }, "where": [["ownerId", "==", "u1"]] }
  ]
}
```

Case fields: `name`, `expect` (`allow`/`deny`), `op` (`get list create update delete`), `path`, `auth`
(claims; omit when signed out), `resource` (the existing document or file), `request` (the incoming one),
`where` (database lists: `[field, op, value]` with `== != < <= > >=`), `collectionGroup`, `documents` (added
to the file's) and `time` (`request.time`, ISO 8601; default now). Timestamps, bytes, paths and exact integers
use tagged values: `{"$timestamp": "2026-01-01T00:00:00Z"}`, `{"$bytes": "…"}`, `{"$path": "…"}`,
`{"$integer": "5"}`. Comments are allowed.

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
