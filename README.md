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
winche init --console https://api.example.com/_console   # winche.json + database.rules + storage.rules
winche login                                             # browser sign-in (Keycloak)
winche rules validate                                    # offline: syntax, paths, unknown functions
winche rules diff                                        # local files vs. what the console enforces
winche rules deploy                                      # publish changed files as new versions
winche rules deploy --dry-run                            # show the changes without saving
winche rules pull                                        # write the live rules into the files
winche logout
```

Options: `--console <url>`, `--token <jwt>`, `--config <path>`, `--only database|storage`,
`-m/--message <note>` (deploy), `--force` (deploy unchanged files too), `--stdout` (pull), `--port <n>` (login).
Run `winche --help` for the full list.

## `winche.json`

Found in the working directory or the nearest parent; rule file paths are relative to it.

```json
{
  "console": "https://api.example.com/_console",
  "rules": { "database": "database.rules", "storage": "storage.rules" }
}
```

The rules language is Winche.Rules' (Firestore-style):

```text
match users/{userId} {
  allow read, write: if request.auth.uid == userId;
}
```

## How deploy works

- **All or nothing on validity.** Every file is checked first and errors are reported as `file:line:column`;
  nothing is published if any file has one.
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
