# Winche CLI

`winche` is the command-line tool for [Winche](https://github.com/FlameOfUdun) apps. You keep access rules,
indexes and functions in your repo, check the rules offline, and deploy them to a
[Winche Server](https://github.com/FlameOfUdun/winche-server)'s console. Each deploy is a
new version in the console's history (rules and indexes take the git commit as the note unless you pass `-m`),
so it can be reviewed and reverted there.

## Install

```sh
dotnet tool install -g Winche.Cli     # then: winche --help
dotnet tool update -g Winche.Cli      # later, for a new version
```

It needs the .NET 10 runtime. The current version is 2.0.0.

## Commands

```sh
winche setup --url https://api.example.com               # a new server: setup code, your admin account, first project
winche init --console https://api.example.com/_console   # winche.json, rules files and their test files
winche use --add prod https://api.example.com/_console   # name a console (and use it here)
winche use dev                                           # switch projects; `winche use` lists them
winche login                                             # browser sign-in, once per server (--reauth: again)
winche login --no-browser                                # a code to enter in a browser on any device (SSH, servers)
winche 2fa enable                                        # two-step sign-in for your account (settings need it)
winche rules validate                                    # offline: syntax, paths, unknown functions
winche rules test                                        # offline: run the allow/deny cases
winche rules diff                                        # local files vs. what the console enforces
winche rules deploy                                      # run the tests, then publish changed files as new versions
winche rules deploy --dry-run                            # show the changes without saving
winche rules history                                     # the console's versions (who, when, note)
winche rules history 3 --only database                   # version 3 against the active one
winche rules rollback 3 --only database                  # make version 3 active again
winche rules pull                                        # write the live rules into the files
winche projects                                          # the projects on the console's Winche Server
winche projects:create wards --name Wards                # another project (server admins)
winche products:start database storage                   # start products, following their setup
winche apps:create "Clinic iOS" --apple-bundle-id com.example.clinic   # register an app (also: winche apps)
winche members:add ann@example.com --role Member         # give someone access (also: winche members)
winche settings pull                                     # the project's settings into settings/*.json (also diff, deploy)
winche tokens:create gitlab-main --scopes rules,indexes  # a CI token (also tokens:list, tokens:revoke)
winche logs -f                                           # the project's logs, then new ones as they come
winche users                                             # your apps' users (users:disable, users:enable, users:sign-out)
winche secrets:set smtp-password                         # a secret, typed unseen or from WINCHE_SECRET
winche storage:move --bucket files --region eu-west-1 …  # move the project's files to a bucket (data:move: its database)
winche login:approve BCDF-GHJK                           # allow a CLI waiting with --no-browser, as you
winche server settings pull                              # the server's own settings as files (server admins)
winche indexes deploy -m "why"                           # database.indexes to the console (also diff, pull)
winche functions init                                    # a functions/ folder: index.ts, deno.json, types for VS Code
winche functions deploy -m "what changed"                # deploy it through the console, as you (or a CI token)
winche functions secrets                                 # the secrets functions read (secrets:set NAME, secrets:remove NAME)
winche emulators start                                   # database, storage, auth and functions locally (no console)
winche emulators exec "npm test"                         # …around a command (CI)
winche emulators token u1 role=admin                     # a sign-in token the emulators accept
winche configure --url https://api.example.com           # lib/winche_options.dart for a Flutter/Dart app
winche login:add                                         # another account on the server (login:list, login:use <email>)
winche logout                                            # end the sign-in (logout <email>: just that account)
```

Options: `-P/--project <alias|id>`, `--console <url>`, `--token <token>`, `--config <path>`, `--only database|storage`,
`-m/--message <note>` (deploy), `--force` (deploy unchanged files too), `--skip-tests` (deploy), `-y/--yes`
(rollback without a prompt), `--stdout` (rules and indexes pull), `--port <n>` (login, emulators).
Run `winche --help` for the full list.

## Without the console

Everything a project and the server need can be done from the terminal, from setting a server up to moving its
data. The console is there for looking around (the data browser, files, function runs) and for what's easier on a
page.

```sh
export WINCHE_ADMIN_PASSWORD=...                       # or it's asked for (not shown as you type)
winche setup --url https://api.example.com --code "$(docker compose logs winche | grep -o 'Setup code: .*' | cut -d' ' -f3)" \
  --email ann@example.com --id clinic --name Clinic   # the admin account (signed in here) and the first project
winche 2fa enable                                      # an authenticator app: changing settings needs it by default
winche products:start database storage authentication
winche apps:create "Clinic" --id clinic-app --android-package com.example.clinic --apple-bundle-id com.example.clinic
winche members:add bob@example.com --role Member
winche init --console https://api.example.com/_console
winche settings pull                                   # settings/database.json, storage.json, auth.json, webhooks.json, …
```

- **`winche setup`** does the console's first-run setup: the **setup code** (only in the server's log), your admin
  account, and the first project with its database. That's on the server's PostgreSQL by default, or elsewhere with
  `--db-host`, `--db-port`, `--db-name`, `--db-user`, `--db-ssl` and `WINCHE_DB_PASSWORD`. Anything not passed is asked
  for. Run again after a failure, it carries on where it stopped.
- **Settings as files:** `settings/<area>.json` beside `winche.json`, one per area: `database` (limits, history,
  request logs), `database.live`, `storage`, `auth` (your apps' sign-in methods and emails), `auth.providers`
  (trusted token issuers) and `webhooks`. `winche settings deploy -m "why"` saves each changed file as a new version
  (`--dry-run` to see the changes; `--only auth,webhooks`). A file changed on the server since is refused, not
  overwritten. Secrets stay on the server: the SMTP password, webhook signing secrets, a bucket's keys and the
  server's own settings are set in the console. Indexes and expiry are `database.indexes`, not `database.json`.
- **A webhook** in `settings/webhooks.json` (each gets its signing secret on the server when first saved):

  ```json
  { "hooks": [ {
      "id": "new-users", "name": "New users", "enabled": true,
      "when": { "source": "database", "events": ["created"], "path": "users/{uid}" },
      "do": { "kind": "webhook", "url": "https://example.com/hooks/users" }
  } ] }
  ```
- **Apps:** `winche apps:create <name>` registers an app (`--id`, `--android-package`, `--apple-bundle-id`,
  `--web-origin a,b`; Flutter is the only SDK so far), and prints the `winche configure --app <id>` to run in its
  folder. `winche apps` lists them.
- **Members:** `winche members:add <email> --role Admin|Member|Viewer` (Member by default). In a project made with
  `projects:create`, they become its member; someone new gets an account and a link to set its password. The first
  project goes by the accounts' own roles: someone new is invited with that role, an existing account is given it
  (Admin there is a server admin). Links are emailed when the server can send email, else printed to pass on.
  `winche members` lists who has access.
- **Taking things back:** `winche members:remove <email>` (in the first project: their role, or their pending
  invite; the account stays), `winche invites` / `invites:revoke <email>`, `winche apps:delete <id>`, and for server
  admins `winche projects:archive | projects:restore <id>` and `projects:delete <id>` (archived first; asks you to type
  the id, or `--yes`). Anything that can't be undone asks first; without a terminal it needs `--yes`.
- **Secrets** are never on the command line: they're typed unseen, or read from `WINCHE_SECRET`. `winche secrets`
  lists the project's (`smtp-password`, and `keycloak-sync --provider <name>` for providers kept in step with
  Keycloak); `winche secrets:set <name>` sets one. `winche webhooks:secret <id>` prints a webhook's signing secret
  (`--rotate` makes a new one).
- **Your apps' users:** `winche users [--search ann] [--limit 50]`, `users:disable | users:enable | users:sign-out
  <email or uid>`.
- **Logs:** `winche logs` prints the latest entries, oldest first (`--product database|storage`, `--level warning`,
  `--search …`, `--since 30m|2h|1d`, `--limit`). `-f` / `--follow` keeps printing new ones as they come, until
  Ctrl+C.
- **Moving files:** `winche storage:move` says where the files are. With `--bucket`, `--region`, `--service-url` (for
  S3-compatible services), `--access-key` and `--path-style` (the secret key in `WINCHE_SECRET`), or `--to server`,
  it copies every file there, shows the progress, then switches the project to it. `--finish` ends the move once
  the server's catch-up pass has run (it says when); `--cancel` stops a move before the switch.
- **Moving the database** (server admins): `winche data:move` says where it is; `--db-name` (and `--db-host`,
  `--db-port`, `--db-user`, `--db-ssl`, `WINCHE_DB_PASSWORD`) checks the target, then moves it while the project
  pauses. `--finish` deletes the old copy (`--keep-old` keeps it).
- **The server itself** (server admins): `winche server settings pull | diff | deploy` keeps `settings/server.json`
  (the console's sign-in providers, email) and `settings/server.auth.json` (the console accounts' passwords, two-step,
  sessions, lockout) as files. `winche server secrets` / `server secrets:set <name>` (the console's SMTP password,
  providers' client secrets). `winche server users`, `server users:disable | server users:enable | server
  users:sign-out <email>` for the console's accounts (the last admin can't be turned off).
- **Another machine without a browser:** run `winche login --no-browser` there, and allow its code from a CLI that's
  signed in as you: `winche login:approve <code>`. No browser is needed anywhere.
- **CI tokens:** `winche tokens:create <name> --scopes functions,rules,indexes [--days 90]` shows the token once
  (`--quiet`: only the token, for scripts). `tokens:list`, `tokens:revoke <id or name>`. A CI token can't make tokens.
- **No browser here** (an SSH session, a server): `winche login --no-browser` shows a code and an address. Open it on
  any device, sign in, check the code and allow it.

## Connecting an app

In your Flutter app's folder,

```sh
winche configure --url https://api.example.com --app app-xxxxxxxxxxxx   # or WINCHE_SERVER; neither: the emulators on localhost:9090
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

Optional keys: `"projects"` (below), `"indexes"` (default `database.indexes`), `"functions"` (the functions folder,
default `functions`; `winche functions init` adds it) and `"projectId"` (the emulators' project id).

The rules language is Winche.Rules' — see the **[Rules Language Guide](https://github.com/FlameOfUdun/winche-rules/blob/main/GUIDE.md)**:

```text
match users/{userId} {
  allow read, write: if request.auth.uid == userId;
}
```

## Projects

Name your consoles in `winche.json` and pick one per checkout:

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

A Winche Server can run several projects: the default one at the root (console at `/_console`), the others each at
`/p/<id>` with its console at `/p/<id>/_console`. `winche projects`
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
- **Values:** `'strings'`, numbers, `true false null`, `[arrays]`, `{maps}` (keys bare or quoted),
  `timestamp('…')`, `path('…')` (a document reference), `fileReference('scans/a.png')`, `userReference('u1')`,
  `geoPoint(51.5, -0.12)` (latitude, longitude), `bytes('base64')`. `//` comments.
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
- **Live straight away.** The console hot-swaps the new rules on every server instance, and the deployed version
  stays in force after a restart.

## Signing in

`winche login` opens your browser: you sign in to the server's console (a password or your
company sign-in) and allow the CLI. It uses PKCE and a loopback redirect (`http://127.0.0.1:9005/callback`, or
`--port <n>`; RFC 8252), and saves the sign-in in `~/.config/winche/credentials.json` (or under `$XDG_CONFIG_HOME`),
readable by you only.

- **Once per server.** Accounts belong to the server, so one sign-in covers every project on it
  (`/_console` and `/p/<id>/_console`). Each request runs with your role in that project as it is then.
- **A session of your account.** The CLI keeps a refresh token and renews its short-lived access token as needed. It's
  listed among your account's sessions (**Server settings → Authentication → Users**), and it ends with
  `winche logout`, signing out everywhere, a password change, or the account being turned off.
- **Already signed in,** `winche login` says so. `winche login --reauth` signs in again.
- **No browser on this machine:** `winche login --no-browser` shows a code to enter at `<server>/_console/cli-login`
  on any device (RFC 8628's device flow, with PKCE). The code works once, for 10 minutes.
- **More than one account:** `winche login:add` signs in another beside the one in use, `winche login:list` shows
  them (`*` marks the one in use), and `winche login:use <email>` switches. `winche logout <email>` ends one;
  `winche logout` ends them all.
- Deploying needs the console's **Admin** role (and two-step sign-in, where the console requires it for settings).
- **CI:** an admin makes a **CI token** (`winche tokens:create`, or **Project settings → CI tokens**): named (e.g. `gitlab-main`), with the
  scopes it may deploy (`functions`, `rules`, `indexes`), expiring (30 days to 2 years), revocable. Set `WINCHE_TOKEN`
  to it, or pass `--token` (and the console with `--console` or `WINCHE_CONSOLE`): `winche functions deploy`,
  `winche rules deploy` and `winche indexes deploy` then run as that token, named in the version history
  ("gitlab-main (CI token by ann@example.com)"). It stops working if its maker is no longer an admin, and can't do
  anything but deploy. `winche functions deploy` exits with 1 when the code doesn't bundle, printing the problems.

### Function secrets

Functions read API keys and passwords with `ctx.secrets.get("NAME")`, declaring them in their options
(`{ secrets: ["NAME"] }`). They're kept encrypted on the server and never shown again:

```sh
winche functions secrets                                  # each one: set or not, when, by whom, which functions use it
winche functions secrets:set MAIL_API_KEY                 # add or replace: typed unseen, or from WINCHE_SECRET
winche functions secrets:set FIREBASE_SA --from-file sa.json   # a file's contents (a service account's JSON)
winche functions secrets:remove MAIL_API_KEY
```

Setting one applies from the functions' next run; there's nothing to deploy. It needs the Admin role (and two-step
sign-in where the console requires it); a CI token can't. In the emulators, secrets come from `.secrets.local` in the
functions folder (`NAME=value` lines, reloaded on save, never deployed; `winche functions init` adds it to
`.gitignore`).

## Emulators

`winche emulators start` runs Winche.Database (REST and the live WebSocket), Winche.Storage, Winche Auth and functions in
one local process, for your app to talk to. There's no console in it: the console is part
of Winche Server.

```text
Winche emulator is running — rules from database.rules, storage.rules, reloaded on save

  Database (REST)   http://localhost:9090/documents/v1
  Database (live)   ws://localhost:9090/documents/v1/ws
  Storage           http://localhost:9090/files/v1
  Auth              http://localhost:9090/auth/v1   (sign up, sign in, anonymous; or `winche emulators token <uid>`)
  Functions         http://localhost:9090/functions/v1/call/<name>   (from the functions folder; winche functions init makes one)
  Postgres          temporary (gone when the emulator stops)

  Sign in as anyone with an unsigned token:
    Authorization: Bearer $(winche emulators token u1 role=admin)

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
- **Functions** come from the functions folder (`"functions"` in `winche.json`, default `functions/`) and are
  reloaded on save; a version that doesn't bundle is reported and the last good one keeps running. They run on Deno:
  `WINCHE_DENO`, else the one on `PATH`, else a pinned release downloaded once into `~/.config/winche/deno`.
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

- A [Winche Server](https://github.com/FlameOfUdun/winche-server) (the console is part of it).
- .NET 10 SDK.

The CLI talks to the console's HTTP API (`/_console/api/...`, or `/p/<id>/_console/api/...` for a project):
`api/rules`, `api/functions`, `api/settings/database/indexes`, `api/projects`, and `api/cli-login` (approve, token,
refresh, logout) for signing in.

## Develop

```sh
dotnet build Winche.Cli.slnx
dotnet test Winche.Cli.slnx      # needs Docker (Testcontainers Postgres)
```

The tests need a [winche-server](https://github.com/FlameOfUdun/winche-server) checkout next to this repository: they run
the CLI in-process against `tests/Winche.Cli.TestHost`, which hosts the console's API from there with its own
accounts, including a full browser sign-in (the tests play the browser).

Releases: push a `v*` tag; GitHub Actions packs and publishes to NuGet.

## License

See [LICENSE](LICENSE).
