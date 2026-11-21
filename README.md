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

It needs the .NET 10 runtime. The current version is 2.2.0.

## Commands

```sh
winche setup --url https://api.example.com               # a new server: setup code, your admin account
winche init --console https://api.example.com --project clinic   # winche.json, rules files and their test files
winche use --add prod https://api.example.com/projects/clinic   # name a project (and use it here)
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
winche apps:create "Clinic iOS" --apple-bundle-id com.example.clinic --apple-team-id ABCDE12345   # add an app (also: winche apps)
winche integrity:set database enforce -m "Our apps send tokens"   # Integrity per product (also: winche integrity)
winche members:add ann@example.com --role Member         # give someone access (also: winche members)
winche settings pull                                     # the project's settings into settings/*.json (also diff, deploy)
winche accounts:create gitlab-main --scopes rules,indexes  # a service account for CI (accounts, accounts:keys:add, accounts:trust)
winche logs -f                                           # the project's logs, then new ones as they come
winche users                                             # your apps' users (users:disable, users:enable, users:sign-out)
winche secrets                                           # the project's secrets: functions' and plugins', and the products' own
winche secrets:set MAIL_KEY                              # add or replace one, typed unseen or from WINCHE_SECRET (or --ref aws-ssm:/app/key)
winche secrets:sources                                   # where secrets can be read from (AWS), the project's own key (secrets:key aws, secrets:test)
winche storage:move --bucket files --region eu-west-1 …  # move the project's files to a bucket (data:move: its database)
winche login:approve BCDF-GHJK                           # allow a CLI waiting with --no-browser, as you
winche server settings pull                              # the server's own settings as files (server admins)
winche indexes deploy -m "why"                           # database.indexes to the console (also diff, pull)
winche functions init                                    # a functions/ folder: index.ts, deno.json (the SDKs from npm)
winche functions deploy -m "what changed"                # deploy it through the console, as you (or a service account)
winche assets init                                       # an assets/ folder, assets.rules and its tests, in winche.json
winche assets deploy -m "German"                         # publish the folder as a release; its channel points at it
winche assets diff                                       # the folder against the channel's release
winche assets channels:set production 12                 # point a channel at a release (an older one: a rollback)
winche emulators start                                   # database, storage, auth, functions and assets locally (no console)
winche emulators exec "npm test"                         # …around a command (CI)
winche emulators token u1 role=admin                     # a sign-in token the emulators accept
winche configure --url https://api.example.com           # lib/winche_options.dart for a Flutter/Dart app
winche login:add                                         # another account on the server (login:list, login:use <email>)
winche logout                                            # end the sign-in (logout <email>: just that account)
```

Options: `-P/--project <alias|id>`, `--console <server or project address>`, `--token <token>`, `--config <path>`, `--only database|storage|assets`,
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
  --email ann@example.com                              # the admin account (signed in here)
winche 2fa enable                                      # an authenticator app: changing settings needs it by default
winche projects:create clinic --name Clinic            # a project: its own database, at /projects/clinic
winche use --add clinic https://api.example.com/projects/clinic
winche products:start database storage authentication
winche apps:create "Clinic" --id clinic-app --android-package com.example.clinic --apple-bundle-id com.example.clinic
winche members:add bob@example.com --role Member
winche init --console https://api.example.com --project clinic
winche settings pull                                   # settings/database.json, storage.json, auth.json, webhooks.json, …
```

- **`winche setup`** does the server's first-run setup: the **setup code** (only in the server's log) and your admin
  account. The server then runs with no project: `winche projects:create <id>` makes each one, its database on the
  server's PostgreSQL by default, or elsewhere with `--db-host`, `--db-port`, `--db-name`, `--db-user`, `--db-ssl` and
  `WINCHE_DB_PASSWORD`. Anything not passed is asked for. A project command pointed at the server's own console (the
  root) says to pick a project: `--project <id>`, or `winche use`.
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
- **Apps:** `winche apps:create <name>` adds an app (`--id`, `--android-package`, `--apple-bundle-id`,
  `--apple-team-id`; Flutter is the only SDK so far), and prints the `winche configure --app <id>` to run in its
  folder. `winche apps` lists them with today's verified requests.
- **Integrity:** apps prove they're the project's own and send an Integrity token with every request. `winche integrity`
  shows each product's mode (`off`, `monitor`: counted, never refused, the default; `enforce`: refused without a
  token) with today's requests, how long tokens last, and the debug tokens. `winche integrity:set <product>
  off|monitor|enforce -m "why"` changes one (`--token-minutes <30-1440>` for the lifetime).
  `winche integrity:debug-tokens:create <app> <name>` prints a debug token once (for simulators, desktop builds and
  CI; on stdout alone, for scripts), `integrity:debug-tokens:delete <id>` removes one.
- **Members:** `winche members:add <email> --role Admin|Member|Viewer` (Member by default). In a project made with
  `projects:create`, they become its member; someone new gets an account and a link to set its password. Links are emailed when the server can send email, else printed to pass on.
  `winche members` lists who has access.
- **Taking things back:** `winche members:remove <email>` (off the project's members; the account stays), `winche invites` / `invites:revoke <email>`, `winche apps:delete <id>`, and for server
  admins `winche projects:archive | projects:restore <id>` and `projects:delete <id>` (archived first; asks you to type
  the id, or `--yes`). Anything that can't be undone asks first; without a terminal it needs `--yes`.
- **Secrets** are never on the command line: they're typed unseen, or read from `WINCHE_SECRET`. `winche secrets`
  lists the project's, as the console's Secrets page does: the ones functions and plugins read (`MAIL_KEY`,
  `SMS__AUTH_TOKEN`), and the products' own (`smtp-password`, and `keycloak-sync --provider <name>` for providers kept
  in step with Keycloak). `winche secrets:set <name>` sets one, `winche secrets:remove <NAME>` removes a functions or
  plugin one. (`winche functions secrets…`, the old names, still work.) `winche webhooks:secret <id>` prints a webhook's signing secret
  (`--rotate` makes a new one).
- **Your apps' users:** `winche users [--search ann] [--limit 50]`, `users:disable | users:enable | users:sign-out
  <email or uid>`.
- **Logs:** `winche logs` prints the latest entries, oldest first (`--product database|storage`, `--level warning`,
  `--search …`, `--since 30m|2h|1d`, `--limit`). `-f` / `--follow` keeps printing new ones as they come, until
  Ctrl+C.
- **Moving files:** `winche storage:move` says where the files are. With `--bucket`, `--region`, `--service-url` (for
  S3-compatible services), `--access-key` and `--path-style` (the secret key in `WINCHE_SECRET`), or `--to server`,
  it copies every file there (an AWS bucket without `--access-key` is reached with the server's own AWS permissions,
  its role or environment: server admins only), shows the progress, then switches the project to it. `--finish` ends the move once
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
- **Service accounts:** `winche accounts:create <name> --scopes functions,rules,indexes` (CI) or `--role member` (a server);
  `accounts:keys:add <name> [--days 90]` shows a key once (`--quiet`: only the key, for scripts); `accounts`,
  `accounts:keys:revoke`, `accounts:trust`, `accounts:disable`, `accounts:delete`. A service account can't manage them.
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
(**Integrity**, or `winche apps`): it goes in the file as `appId`, and the SDK uses it to get the app's Integrity
tokens. Then:

```dart
final app = await WincheFlutter.initializeApp(options: DefaultWincheOptions.currentPlatform);
if (kDebugMode) app.useEmulator();   // winche emulators start
```

The console's Project settings → General (Add app) shows the same file, to download. The emulators' project id is `projectId` in
`winche.json` (default `demo`).

## `winche.json`

Found in the working directory or the nearest parent; rule file paths are relative to it.

```json
{
  "server": "https://api.example.com",
  "project": "clinic",
  "rules": { "database": "database.rules", "storage": "storage.rules" },
  "tests": { "database": "database.rules.test", "storage": "storage.rules.test" }
}
```

Optional keys: `"projects"` (below), `"indexes"` (default `database.indexes`), `"functions"` (the functions folder,
default `functions`; `winche functions init` adds it), `"assets"` (`{ "folder": "assets", "channel": "production" }`;
`winche assets init` adds it, with `"assets": "assets.rules"` under `"rules"`), `"plugins"` (below) and `"projectId"`
(the emulators' project id).

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
  "projects": { "dev": "http://localhost:5000/projects/clinic", "prod": "https://api.example.com/projects/clinic" },
  "rules": { "database": "database.rules" }
}
```

`winche use <alias>` picks the project for this checkout (kept on your machine, not in the repo, so each person
and CI job picks its own); `winche use` lists them (`*` is the picked one); `winche use --add <alias> <url>`,
`--remove <alias>` and `--clear` edit them. Every command that talks to a console prints `Project: prod (…)` first.
Which project a command uses: `--console`, else `-P/--project`, else `WINCHE_CONSOLE`, else the picked project,
else the only project, else `"server"` and `"project"`. Sign-ins are kept per server, so switching projects doesn't
sign you out.

A Winche Server runs several projects, each at `/projects/<id>` (its apps' address); its console is at `/console`.
`winche projects` lists the ones on the server that you can use (and, for server admins, the archived ones). Name them
with `winche use --add <alias> https://api.example.com/projects/<id>`; without any aliases in `winche.json`,
`--project <id>` reaches a project on the same server directly. For an app,
`winche configure --url https://api.example.com --project <id>`.

**From CLI 2.x (Winche Server 2.x):** addresses before Winche Server 3.0 (`…/_console`, `…/p/<id>/_console`) are
refused, with what to write instead: `"console"` in `winche.json` becomes `"server"` and `"project"`, aliases become
the projects' addresses, and you sign in again (`winche login`).

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
edits the same text. Service accounts need the `indexes` scope.

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
  Each request runs with your role in that project as it is then.
- **A session of your account.** The CLI keeps a refresh token and renews its short-lived access token as needed. It's
  listed among your account's sessions (**Server settings → Authentication → Users**), and it ends with
  `winche logout`, signing out everywhere, a password change, or the account being turned off.
- **Already signed in,** `winche login` says so. `winche login --reauth` signs in again.
- **No browser on this machine:** `winche login --no-browser` shows a code to enter at `<server>/console/cli-login`
  on any device (RFC 8628's device flow, with PKCE). The code works once, for 10 minutes.
- **More than one account:** `winche login:add` signs in another beside the one in use, `winche login:list` shows
  them (`*` marks the one in use), and `winche login:use <email>` switches. `winche logout <email>` ends one;
  `winche logout` ends them all.
- Deploying needs the console's **Admin** role (and two-step sign-in, where the console requires it for settings).
- **CI:** an admin makes a **service account** (`winche accounts:create`, or **Project settings → Service accounts**):
  named (e.g. `gitlab-main`), with the scopes it may deploy (`functions`, `rules`, `indexes`, `assets`). It signs in
  with a **trusted identity**, the CI job's own token (`winche accounts:trust gitlab-main --issuer https://gitlab.example.com
  --subject 'project_path:team/app:ref_type:branch:ref:main' --audience <aud>`, then `WINCHE_ID_TOKEN` in the job: no
  secret to keep), or a **key** (`winche accounts:keys:add`, then `WINCHE_TOKEN`; at most two, so one replaces the other
  with no downtime). The CLI exchanges either for an hour's access token. Each deploy is named for it in the version
  history ("gitlab-main (service account)"); it belongs to the project, and the console flags it if its maker is no
  longer an admin. `winche functions deploy` exits with 1 when the code doesn't bundle, printing the problems. CI tokens
  from before are service accounts now: their `WINCHE_TOKEN` keeps working (the CLI exchanges it).

`winche functions deploy` also sends the project's console pages: the web files (`.html`, `.css`, `.js`, `.svg`, images,
fonts) of the folders `pages({ files })` names. Binary files go as base64.

### Console pages with a build (React, Vite, …)

A page can be a front-end project of its own. Name it in `winche.json`:

```json
"pages": { "console/reports": { "source": "pages/reports" } }
```

The key is the folder `pages({ files })` names; `build` (default `npm run build`) and `output` (default `dist`) say how
it's built. `winche functions deploy` runs each page's build and deploys its output as that folder (`--no-build`
deploys the last output; `winche pages build` only builds). With `winche emulators start`, run the page's build in
watch mode: the emulator redeploys when its output changes.

In a page, `import { documents } from "@winche-technologies/winche-admin"` acts as the person who opened it (their role in the project
limits it). The page brings the SDK like any dependency (bundled with it; with Vite, `base: "./"`, since a page's files
are addressed relative to it); the console gives it only the channel the SDK finds by itself.

### Secrets

Functions read API keys and passwords through handles: `const key = secrets.define("NAME")` at the top, listed in the
functions' options (`{ secrets: [key] }`), read with `key.value()`. They're kept encrypted on the server and never shown again:

```sh
winche secrets                                            # each one: set or not, when, by whom, which functions use it
winche secrets:set MAIL_API_KEY                           # add or replace: typed unseen, or from WINCHE_SECRET
winche secrets:set SERVICE_ACCOUNT --from-file sa.json    # a file's contents (a service account's JSON)
winche secrets:set SES_SECRET --ref aws-sm:app/ses#secretAccessKey   # read from AWS when functions run
winche secrets:remove MAIL_API_KEY
```

A secret can instead be a **reference** to where it's really kept, read when it's needed (and again a few minutes
later, so rotations are picked up): `aws-sm:<ARN or name>[#field]` (AWS Secrets Manager, optionally one field of a JSON
secret) or `aws-ssm:<parameter>` (Parameter Store). `--ref` works for `secrets:set` and `server secrets:set` too.

```sh
winche secrets:sources                                    # where secrets can be read from, and the project's own keys
winche secrets:key aws --access-key-id AKIA… --region eu-west-2   # the project's AWS key (secret key: WINCHE_SECRET or typed)
winche secrets:test aws-ssm:/app/mail-key                 # read it now: whether it works (never the value)
```

On Winche Server, references without the project's own AWS key are read with the server's credentials, so only server
admins may set those.

Setting one applies from the functions' next run; there's nothing to deploy. It needs the Admin role (and two-step
sign-in where the console requires it); a service account can't. In the emulators, secrets come from `.secrets.local` in the
functions folder (`NAME=value` lines, reloaded on save, never deployed; `winche functions init` adds it to
`.gitignore`).

### Plugins

Plugins are packages of functions (on npm) that a project installs instead of writing them, each copy under an id
with its own settings and secrets. Its functions are `<id>.<name>` (apps call `functions.call("sms.send", …)`); its
secrets are `<ID>__<NAME>`.

```sh
winche plugins:info text-plugin                           # its settings, secrets and what it does
winche plugins:install text-plugin@1.2.0 --id sms --set FROM=+441234567890 --set QUEUE=sms
winche secrets:set SMS__AUTH_TOKEN                        # its secret, as any other
winche plugins                                            # installed copies, newer versions, secrets still needed
winche plugins:configure sms --set QUEUE=texts
winche plugins:upgrade sms                                # the newest (or sms@2.0.0); settings kept
winche plugins:uninstall sms --remove-secrets             # its functions go; what it wrote stays
```

Or list them in `winche.json`, and `functions deploy` installs exactly these (the emulators run them too):

```json
"plugins": { "sms": { "package": "text-plugin", "version": "1.2.0", "settings": { "FROM": "+441234567890" } } }
```

Without `"plugins"`, a deploy keeps whatever is installed. With it, plugins installed or changed in the console that
the deploy would remove or change are listed first and asked about; in CI (nobody to answer) the deploy is refused
unless `--force`. Each change is a new functions version, rolled back with the code.

## Assets

Content your apps download and keep offline (translations, pages, images, videos) lives in a folder of your repository
and is published as numbered releases. A channel (`production`, `beta`, one per app version…) points apps at one:

```sh
winche assets init                          # assets/translations/en.json, assets.rules and assets.rules.test
winche rules deploy --only assets           # who may read which paths (nothing is readable until a rule allows it)
winche assets deploy -m "German"            # changes from the channel's release, then a new release it points at
winche assets deploy --channel beta         # another channel
winche assets diff                          # + added, ~ changed, - removed, against the channel's release
winche assets releases                      # newest first, with the channels on each
winche assets release 12                    # its files
winche assets channels                      # each channel's release
winche assets channels:set production 11    # back to 11 (asks first; -y doesn't)
winche assets channels:remove beta          # its releases stay
```

Deploy hashes every file and sends only what the server doesn't have, streamed, so a large video goes up once whatever
the releases using it. A folder that's what the channel already has publishes nothing (`--force` makes a new release
anyway). CI deploys as a service account scoped to `assets` (`winche accounts:create ci --scopes assets,rules`).

**Files on a CDN.** Large files can stay on a CDN that signs links (CloudFront: set it up in the console's Assets →
Settings → CDNs). List them in `assets.cdn.json` beside `winche.json` (or `"cdn"` in winche.json's `assets`), path →
address:

```json
{ "files": { "videos/adult/bvm.mp4": "https://cdn.example.com/app/adult/bvm.mp4" } }
```

`winche assets deploy` publishes them with the folder, uploading nothing for them: the server reads each new or changed
one once through a signed link for its hash. Apps never see the addresses; each read is the rules, then a link the CDN
takes for a few minutes. Give a replaced file a new address: a release names its content by hash.

**Releases made in the console** (Assets → Files → New release) can have files the folder doesn't. Deploying the folder
over one lists what it would take out and asks first; in CI it refuses unless `--force`.

## Emulators

`winche emulators start` runs Winche.Database (REST and the live WebSocket), Winche.Storage, Winche Auth, functions and
assets (the assets folder published to its channel at start and on every save, `/assets/v1`) in one local process, for your app to talk to, at the project's address as on Winche Server (`/projects/<id>`, the id
from `"projectId"`, default `demo`; the root answers too). There's no console in it: the console is part of Winche
Server.

```text
Winche emulator is running — rules from database.rules, storage.rules, reloaded on save

  Project           http://localhost:9090/projects/demo   (apps' base URL)
  Database (REST)   http://localhost:9090/projects/demo/documents/v1
  Database (live)   ws://localhost:9090/projects/demo/documents/v1/ws
  Storage           http://localhost:9090/projects/demo/files/v1
  Auth              http://localhost:9090/projects/demo/auth/v1   (sign up, sign in, anonymous; or `winche emulators token <uid>`)
  Functions         http://localhost:9090/projects/demo/functions/v1/call/<name>   (from the functions folder; winche functions init makes one)
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
  Their memory and instances (`memory`, `minInstances`, `maxInstances`, `reservedInstances`, `isolate`) apply as on a
  server: an instance over its memory, or a process out of memory, is reported in the output.
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
when signed in). The Winche VS Code extension
([Marketplace](https://marketplace.visualstudio.com/items?itemName=FlameOfUdun.winche),
[source](https://github.com/FlameOfUdun/winche-vscode)) starts it, and turns Deno on for the functions folder named in
`winche.json`, so `@winche-technologies/winche-functions` resolves in the editor.

## Requirements

- A [Winche Server](https://github.com/FlameOfUdun/winche-server) (the console is part of it).
- The .NET 10 runtime; Deno for `winche functions` and the emulators' functions.

The CLI talks to the console's HTTP API, `/console/v1/…`: a project's areas under `/console/v1/projects/<id>/`
(`rules`, `functions`, `settings/database/indexes`, `assets`, `tokens`, …), the server's at `/console/v1/`
(`projects`, `accounts`, `invites`, and `cli` (approve, token, refresh, logout) for signing in).

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
