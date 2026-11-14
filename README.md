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
winche login                                             # browser sign-in (the console's Keycloak, or its own accounts)
winche rules validate                                    # offline: syntax, paths, unknown functions
winche rules test                                        # offline: run the allow/deny cases
winche rules diff                                        # local files vs. what the console enforces
winche rules deploy                                      # run the tests, then publish changed files as new versions
winche rules deploy --dry-run                            # show the changes without saving
winche rules history                                     # the console's versions (who, when, note)
winche rules history 3 --only database                   # version 3 against the active one
winche rules rollback 3 --only database                  # make version 3 active again
winche rules pull                                        # write the live rules into the files
winche functions init                                    # a functions/ folder: index.ts, deno.json, types for VS Code
winche functions deploy -m "what changed"                # deploy it through the console, as you (or a CI token)
winche emulators start                                   # database, storage, auth and functions locally
winche emulators exec "npm test"                         # …around a command (CI)
winche emulators token u1 role=admin                     # a sign-in token the emulators accept
winche configure --url https://api.example.com          # lib/winche_options.dart for a Flutter/Dart app
winche logout
```

Options: `-P/--project <alias>`, `--console <url>`, `--token <token>`, `--config <path>`, `--only database|storage`,
`-m/--message <note>` (deploy), `--force` (deploy unchanged files too), `--skip-tests` (deploy), `-y/--yes`
(rollback without a prompt), `--stdout` (pull), `--port <n>` (login).
Run `winche --help` for the full list.

## Connecting an app

Like `flutterfire configure`: in your Flutter app's folder,

```sh
winche configure --url https://api.example.com --app app-xxxxxxxxxxxx   # or no --url: the emulators on localhost:9090
```

reads the server's public setup (`/.well-known/winche`: which services it runs and where, and the sign-in providers'
client ids) and writes `lib/winche_options.dart`, with a `DefaultWincheOptions` for each platform folder the app has
(`--platforms android,ios,web` to choose, `--out` to write elsewhere). `--app` is the app's id from the console
(Project settings → Your apps): it goes in the file as `appId`, the SDK sends it with every request, and the console
counts each app's requests (and can refuse apps that aren't registered). Then:

```dart
final app = await WincheFlutter.initializeApp(options: DefaultWincheOptions.currentPlatform);
if (kDebugMode) app.useEmulator();   // winche emulators start
```

The console's Project settings → Your apps shows the same file, to download. The emulators' project id is `projectId` in
`winche.json` (default `demo`).

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

A Winche Server can run several projects, each at `/p/<id>` with its console at `/p/<id>/_console`. `winche projects`
lists the ones on the current console's server that you can use (and, for server admins, the archived ones). Name
them with `winche use --add <alias> https://api.example.com/p/<id>/_console`; without any aliases in `winche.json`,
`--project <id>` reaches a project on the same server directly. Each project's console has its own sign-in for the
CLI (`winche login --project <id>`). For an app, `winche configure --url https://api.example.com --project <id>`.

## Indexes and expiry

The database's indexes and expiry rules, as a file like the rules (`database.indexes`, or `"indexes"` in `winche.json`):

```
// Sessions by user, newest first; gone once they expire.
collection sessions {
  index (userId, startedAt desc)
  expire when expiresAt
}

// Every "notes" collection, wherever it is.
collection group notes {
  index (tags contains)
}
```

`winche indexes pull` writes the console's to the file, `winche indexes diff` shows what deploying it would change, and
`winche indexes deploy -m "why"` checks it on the console and saves it there (the note goes in the history). Indexes
no longer listed are dropped; they build in the background. The console's Database → Settings → Indexes and expiry
edits the same text. CI tokens need the `indexes` scope.

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

`winche login` opens your browser, like `firebase login`, and saves the sign-in in
`~/.config/winche/credentials.json`, readable by you only. Both use PKCE and a loopback redirect
(`http://127.0.0.1:9005/callback`, or `--port <n>`; RFC 8252).

- **A console with its own accounts:** the console asks you (signed in there) to allow the CLI. It then acts as you,
  with your role as it is at each request, for 90 days. `winche logout` ends it; admins see and can end everyone's
  under **Project settings → CLI and CI**.
- **A Keycloak console:** you sign in with the console's Keycloak client; its tokens are refreshed as needed.
  Register the redirect URI on that client.
- Deploying needs the console's **Admin** role (and two-step sign-in, where the console requires it for settings).
- **CI:** an admin makes a **CI token** under **Project settings → CLI and CI**: named (e.g. `gitlab-main`), limited to deploying
  functions and/or rules, expiring (30 days to 2 years), revocable. Set `WINCHE_TOKEN` to it (and the console with
  `--console` or `WINCHE_CONSOLE`): `winche functions deploy` and `winche rules deploy` then run as that token, named
  in the version history ("gitlab-main (CI token by ann@example.com)"). It stops working if its maker is no longer an
  admin, and can't do anything but deploy.

## Emulators

`winche emulators start` runs Winche.Database (REST and the live WebSocket), Winche.Storage, Winche Auth and functions in
one local process, for your app to talk to — like Firebase's emulators. There's no console in it: the console is part
of Winche Server.

```text
Winche emulator is running — rules from database.rules, storage.rules, reloaded on save

  Database (REST)   http://localhost:9090/documents/v1
  Database (live)   ws://localhost:9090/documents/v1/ws
  Storage           http://localhost:9090/files/v1
  Auth              http://localhost:9090/auth/v1
  Functions         http://localhost:9090/functions/v1/call/<name>
  Postgres          temporary (gone when the emulator stops)

14:02:11 PUT    /documents/v1/users/u1 as u1 → 200
14:02:12 GET    /documents/v1/users/u1 as u2 → 403  ← denied by the rules
14:02:30 ↻ database.rules reloaded
```

- **Postgres** starts in Docker for the session (or pass `--pg <connection string>` to use your own and keep the
  data). **Files** go to a temporary folder (or `--data <dir>`), uploaded and downloaded through the emulator's own
  URLs, the way S3's presigned URLs work in production.
- **Rules** come from the files in `winche.json` and are re-applied the moment you save; a file with an error is
  reported and the previous rules stay in force.
- **Sign-in:** any JWT is accepted without checking its signature, and its claims are the caller's (`uid` from `uid`,
  else `sub`). `winche emulators token <uid> [claim=value …]` prints an unsigned one, for `Authorization: Bearer …`
  or the WebSocket's `?access_token=`.
- **Each request** is printed with who made it and the result; `--quiet` turns that off, `--verbose` adds the
  servers' own logs.
- **The console** has the Logs tab, the Database / Storage Logs sub-tabs (Information and up) and the Database History sub-tab (who changed what); every request is logged to the Logs Explorer.
- **Auth:** Winche Auth runs too — sign up, sign in and guests at `/auth/v1` (passwords of 6+ characters), Hand-made tokens from `winche emulators token <uid>` keep working beside real ones. Emails (verify, reset, email change) aren't sent: their links are printed in the emulator's output. Google and Apple sign-in take fake ID tokens: `winche emulators idtoken google ann@example.com`.
- `--port` (default 9090) and `--host` (default `localhost`; `0.0.0.0` to reach it from a phone or another machine).

`winche emulators exec "<command>"` starts the emulators, runs the command with `WINCHE_EMULATOR`,
`WINCHE_EMULATOR_HOST`, `WINCHE_DATABASE_URL` and `WINCHE_STORAGE_URL` set, stops them, and exits with the command's
exit code — for running an app's integration tests in CI.

Needs Docker (unless `--pg`) and the .NET 10 runtime with ASP.NET Core.

## Editors

`winche lsp` is a Language Server Protocol server over stdin/stdout for `.rules` and `.rules.test` files:
errors and warnings as you type (the same checks as `rules validate`), test results on each `test` block and
on failing lines (re-run as you edit either file), formatting (left alone when the file has comments, which
the console doesn't keep), and the document fields the console samples for a match pattern (`winche/fields`,
when signed in). The [Winche VS Code extension](https://github.com/FlameOfUdun/winche-vscode) starts it.

## Requirements

- A host app running Winche.Console with `AddWincheConfig` (where CLI sign-ins and CI tokens are kept), the rules
  editor enabled for rules (`o.AddDatabaseTab(b => b.UseRulesEditor())`, `o.AddStorageTab(b => b.UseRulesEditor())`)
  and Winche.Functions for functions.
- .NET 10 SDK.

The CLI talks to the console's HTTP API (`api/rules`, `api/functions`).

## Develop

```sh
dotnet build Winche.Cli.slnx
dotnet test Winche.Cli.slnx      # needs Docker (Testcontainers Postgres)
```

The tests need a [winche-server](https://github.com/FlameOfUdun/winche-server) checkout next to this repository: they run
the CLI in-process against `tests/Winche.Cli.TestHost`, which hosts the console's API from there in Keycloak mode,
including a full browser sign-in against a fake Keycloak.

Releases: push a `v*` tag; GitHub Actions packs and publishes to NuGet.

## License

See [LICENSE](LICENSE).
