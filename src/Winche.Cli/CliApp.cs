using System.Reflection;
using Winche.Rules;
using Winche.Rules.Evaluation;
using Winche.Rules.Expressions;
using Winche.Rules.Json;
using Winche.Rules.Text;

namespace Winche.Cli;

/// <summary>The <c>winche</c> command line.</summary>
internal static partial class CliApp
{
    private const string Usage = """
        Usage: winche <command> [options]

        Commands:
          init                 Create winche.json, starter database.rules / storage.rules and their test files
          use [<alias>]        Pick the project (console) to use here, or list them
                               (--add <alias> <url>, --remove <alias>, --clear)
          setup --url <server> Set a new server up: the setup code (in its log) and your admin account (--code, --email,
                               --first-name, --last-name; the password in WINCHE_ADMIN_PASSWORD, or asked). Then
                               winche projects:create <id> makes its projects
          login                Sign in to the server in your browser, once for all its projects (--reauth: again;
                               --no-browser: a code to enter in a browser on any device)
          login:add            Sign in another account on the server, beside the one in use
          login:list           The accounts signed in, by server (* is the one in use)
          login:use <email>    Use that signed-in account on the server
          logout [<email>]     End that account's sign-in on the server (none named: all of them)
          2fa enable           Turn on two-step sign-in for your account (an authenticator app)
          rules validate       Check the rules files (offline)
          rules test           Run the allow/deny cases in the test files (offline)
          rules deploy         Run the tests, then publish the rules files as new versions on the console
          rules history [<v>]  List the console's rule versions, or show version <v> against the active one
          rules rollback <v>   Make version <v> active again (saved as a new version)
          rules diff           Show how the rules files differ from what the console enforces
          rules pull           Write the console's live rules into the rules files
          projects             The projects on the console's Winche Server that you can use
          projects:create <id> Make a project (server admins; --name, and --db-name/--db-host/--db-port/--db-user/
                               --db-ssl with WINCHE_DB_PASSWORD for a database elsewhere)
          apps                 The project's apps (Integrity), with today's verified requests
          apps:create <name>   Add an app (--id, --sdk flutter, --android-package, --apple-bundle-id,
                               --apple-team-id, --apple-development to accept App Attest from development
                               builds); then winche configure --app <id>
          integrity            Each product's Integrity mode and today's requests, token lifetime, debug tokens
          integrity:set <product> off|monitor|enforce
                               How a product treats requests without a valid Integrity token (-m why;
                               --token-minutes <30-1440> for how long tokens last)
          integrity:debug-tokens:create <app> <name>
                               A debug token for simulators, desktop builds or CI (printed once)
          integrity:debug-tokens:delete <id>
                               Remove one: the Integrity tokens it got stop working
          members              Who can use the project, and as what
          members:add <email>  Add someone (--role Admin|Member|Viewer, default Member); new people get a link
                               to set their password (emailed when the server can send email)
          products             The project's products, and whether they're started
          products:start <p>…  Start products (database, storage, authentication, functions, assets) and follow their setup
          members:remove <email>  Take someone's access away (off the project's members)
          invites, invites:revoke <email>  Pending invites (an app's own console; a server's projects use members:add)
          apps:delete <id>     Remove a registered app
          projects:archive | projects:restore | projects:delete <id>
                               Archive a project (refused until restored), or delete an archived one for good
          secrets              The project's secrets, as the console's Secrets page lists them: the ones functions and
                               plugins read (set or not, which functions use them) and the products' own (sign-in email,
                               Keycloak sync). Never their values
          secrets:set <name>   Add or replace one: a functions or plugin secret by its NAME (MAIL_KEY, SMS__AUTH_TOKEN), or
                               smtp-password, keycloak-sync --provider <name>. The value from WINCHE_SECRET, --from-file
                               <path>, or typed unseen; or --ref aws-sm:<ARN or name>[#field] | aws-ssm:<parameter>: read
                               from AWS instead
          secrets:remove <NAME>
                               Remove a functions or plugin secret
          secrets:sources      Where secrets can be read from besides the server, and the project's own keys for them
          secrets:key <name>   Set the project's own key for a source (aws: --access-key-id, --region; the secret key
                               from WINCHE_SECRET or typed unseen; --clear removes it)
          secrets:test <reference>
                               Read a reference now: whether it works (never the value)
          webhooks:secret <id> A webhook's signing secret (--rotate: a new one)
          users, users:disable | users:enable | users:sign-out <email or uid>
                               Your apps' users (--search, --limit)
          storage:move         Where files are kept; --bucket/--region/--service-url/--access-key (WINCHE_SECRET: the
                               secret key, or --ref aws-ssm:/app/bucket-key) or --to server copies them there, then
                               switches; an AWS bucket without --access-key uses the server's own AWS permissions
                               (server admins); --finish, --cancel
          data:move            Where the project's database is; --db-name/--db-host/… moves it; --finish [--keep-old]
          logs                 The project's logs (--product database|storage, --level, --search, --since 1h,
                               --limit; -f/--follow keeps printing new ones)
          login:approve <code> Allow a CLI waiting with winche login --no-browser, as you
          server settings pull | diff | deploy
                               The server's own settings as files (server admins)
          server secrets, server secrets:set <name>   The server's secrets (WINCHE_SECRET, or typed unseen; or --ref)
          server users, server users:disable | server users:enable | server users:sign-out <email>
                               The console's accounts
          accounts             The project's service accounts (what CI and other servers act as): roles or scopes,
                               keys, trusted identities
          accounts:create <name>
                               --role viewer|member|admin (a server using the data) or --scopes functions,rules,
                               indexes,assets (CI that deploys)
          accounts:keys:add <account>
                               A key, shown once (--days, default 90; --quiet: only it); two at most
          accounts:keys:revoke <account> <key id>
          accounts:trust <account> --issuer <url> --subject <pattern> --audience <aud>
                               Trust a CI's own tokens (OpenID Connect) instead of a key: WINCHE_ID_TOKEN
          accounts:untrust <account> <identity id>
          accounts:disable | accounts:enable | accounts:delete <account>
          settings pull        Write the project's settings into settings/*.json (--only database,storage,auth,…)
          settings diff        How the settings files differ from the project's
          settings deploy      Save the changed settings files as new versions (-m: the note; --dry-run)
          indexes deploy       Save database.indexes (indexes and expiry) on the console (-m: the note in the history)
          indexes diff         What deploying it would change
          indexes pull         Write the console's indexes and expiry to database.indexes (--stdout to print)
          configure            Write lib/winche_options.dart for a Flutter or Dart app, from a Winche server
                               (--url; default the emulators)
          functions init [<dir>]
                               Make a functions folder (default functions/): an example, deno.json (@winche-technologies/winche-functions, @winche-technologies/winche-admin)
          functions deploy [<dir>]
                               Deploy the functions folder through the console, as you (winche login) or a service account
          pages build          Build the console pages with a build in winche.json ("pages"; functions deploy builds them too,
                               --no-build skips it)
          functions secrets, functions secrets:set | secrets:remove
                               The old names of secrets, secrets:set and secrets:remove (they still work)
          plugins              The plugins installed (packages of functions), their newest versions and the secrets they need
          plugins:info <package>[@version]
                               A plugin package's settings, secrets and what it does
          plugins:install <package>[@version] --id <id> [--set KEY=value …]
                               Install a copy as <id> (its functions are <id>.<name>; secrets <ID>__<NAME>)
          plugins:configure <id> --set KEY=value …
          plugins:upgrade <id>[@version] [--set KEY=value …]
                               Newest version unless one is given; settings kept
          plugins:uninstall <id> [--remove-secrets]
                               Its functions go; what it wrote stays
          assets init [<dir>]  Make an assets folder (default assets/), assets.rules and its tests, and add them to winche.json
          assets deploy [<dir>]
                               Publish the assets folder as a new release and move its channel to it (--channel, default
                               winche.json's or production; -m: the note; --dry-run; --force: publish even if unchanged).
                               Only contents the server doesn't have are sent. Files kept on a CDN, listed in
                               assets.cdn.json ({ "files": { "<path>": "https://…" } }), are published by address.
          assets diff [<dir>]  How the folder differs from the channel's release
          assets releases      The releases, newest first
          assets release <n>   A release's files
          assets channels      The channels and the release each points at
          assets channels:set <name> <release>
                               Point a channel at a release (made if new); an older one is a rollback (-y: don't ask)
          assets channels:remove <name>
                               Remove a channel (its releases stay)
          emulators start      Run the database, storage, auth and functions locally (Postgres in Docker; rules and functions reloaded on save)
          emulators exec <cmd> Start the emulators, run <cmd> against them (e.g. your tests), stop, exit with its code
          emulators token <uid> [claim=value …]
                               Print a sign-in token the emulators accept
          emulators idtoken <google|apple> <email>
                               Print a fake Google or Apple ID token for the emulators' /auth/v1/signIn/idp
          lsp                  Run the language server for editors (the Winche VS Code extension starts it)

        Options:
          --console <url>      A project's address, e.g. https://api.example.com/projects/clinic, or a server's
                               (or WINCHE_CONSOLE, or "server" and "project" in winche.json)
          -P, --project <name> A project from winche.json's "projects" (instead of the one picked with winche use), or
                               a project id on the same Winche Server; configure: the project at --url/projects/<id>
          --token <token>      A service account's key (exchanged for an access token) or access token, instead of a
                               saved sign-in (or WINCHE_TOKEN; or WINCHE_ID_TOKEN: the CI job's own token, for an
                               account that trusts its identity)
          --config <path>      winche.json to use (default: the nearest one upward)
          --only <list>        database, storage, assets, comma-separated (default: every file in winche.json)
          -m, --message <text> Note for the deployed versions, or the assets release (deploy)
          --channel <name>     assets deploy | diff: the channel (default winche.json's "assets.channel", or production)
          --dry-run            Show what deploy would change, without saving
          --force              Deploy even when the console already has these rules
          --skip-tests         Deploy without running the tests first
          -y, --yes            Don't ask before a rollback
          --stdout             Print pulled rules instead of writing the files (pull)
          --port <n>           Loopback port for the sign-in callback (login; default 9005), or the emulators' port (9090)
          --host <name>        Emulators: the address to listen on (default localhost; 0.0.0.0 for devices)
          --pg <connection>    Emulators: use this Postgres instead of a temporary one in Docker
          --data <dir>         Emulators: keep stored files here (default: a temporary folder)
          --quiet              Emulators: don't print each request
          --verbose            Emulators: show the servers' own logs
          --out <file>         configure: where to write (default lib/winche_options.dart)
          --platforms <list>   configure: android,ios,macos,web,windows,linux (default: the app's platform folders)
          --app <id>           configure: the app's id, from the console's Project settings → Your apps
          -h, --help           Show this help
          --version            Show the version
        """;

    public static async Task<int> RunAsync(string[] args, CliEnvironment env, CancellationToken ct = default)
    {
        try
        {
            var a = Args.Parse(args);
            if (a.Flag("version"))
            {
                env.Out.WriteLine(typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion.Split('+')[0] ?? "?");
                return 0;
            }
            if (a.Flag("help") || a.Positional.Count == 0)
            {
                env.Out.WriteLine(Usage);
                return a.Positional.Count == 0 && !a.Flag("help") ? 2 : 0;
            }

            using var http = new HttpClient(env.Http, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };
            var command = string.Join(' ', a.Positional.Take(
                a.Positional[0] is "rules" or "emulators" or "functions" or "indexes" or "settings" or "2fa" or "assets" or "pages" ? 2
                : a.Positional[0] == "server" ? (a.Positional.ElementAtOrDefault(1) == "settings" ? 3 : 2) : 1));
            string? Arg(int i) => a.Positional.ElementAtOrDefault(i);
            var run = new Run(env, a, http);
            return command switch
            {
                "init" => run.Init(),
                "use" => run.Use(a.Positional.ElementAtOrDefault(1), a.Positional.ElementAtOrDefault(2)),
                "login" => await run.LoginAsync(add: false, ct),
                "login:add" => await run.LoginAsync(add: true, ct),
                "login:list" => run.LoginList(),
                "login:use" => run.LoginUse(a.Positional.ElementAtOrDefault(1) ?? throw new CliException("Usage: winche login:use <email>", 2)),
                "logout" => await run.LogoutAsync(a.Positional.ElementAtOrDefault(1), ct),
                "rules validate" => run.Validate(),
                "rules test" => await run.TestAsync(ct),
                "rules history" => await run.HistoryAsync(a.Positional.ElementAtOrDefault(2), ct),
                "rules rollback" => await run.RollbackAsync(a.Positional.ElementAtOrDefault(2), ct),
                "rules deploy" => await run.DeployAsync(ct),
                "rules diff" => await run.DiffAsync(ct),
                "rules pull" => await run.PullAsync(ct),
                "configure" => await run.ConfigureAsync(ct),
                "projects" => await run.ProjectsAsync(ct),
                "setup" => await run.SetupAsync(ct),
                "2fa enable" => await run.TwoFactorEnableAsync(ct),
                "projects:create" => await run.ProjectsCreateAsync(a.Positional.ElementAtOrDefault(1), ct),
                "products" => await run.ProductsAsync(ct),
                "apps" => await run.AppsListAsync(ct),
                "apps:create" => await run.AppsCreateAsync(a.Positional.ElementAtOrDefault(1), ct),
                "integrity" => await run.IntegrityAsync(ct),
                "integrity:set" => await run.IntegritySetAsync(a.Positional.ElementAtOrDefault(1), a.Positional.ElementAtOrDefault(2), ct),
                "integrity:debug-tokens:create" => await run.DebugTokenCreateAsync(a.Positional.ElementAtOrDefault(1), a.Positional.ElementAtOrDefault(2), ct),
                "integrity:debug-tokens:delete" => await run.DebugTokenDeleteAsync(Arg(1), ct),
                "members" => await run.MembersListAsync(ct),
                "members:add" => await run.MembersAddAsync(a.Positional.ElementAtOrDefault(1), ct),
                "members:remove" => await run.MembersRemoveAsync(Arg(1), ct),
                "invites" => await run.InvitesAsync(ct),
                "invites:revoke" => await run.InvitesRevokeAsync(Arg(1), ct),
                "apps:delete" => await run.AppsDeleteAsync(Arg(1), ct),
                "projects:archive" => await run.ProjectsChangeAsync("archive", Arg(1), ct),
                "projects:restore" => await run.ProjectsChangeAsync("restore", Arg(1), ct),
                "projects:delete" => await run.ProjectsChangeAsync("delete", Arg(1), ct),
                "secrets" => await run.AllSecretsAsync(ct),
                "secrets:set" => await run.SecretSetAsync(Arg(1) ?? throw new CliException("Usage: winche secrets:set <NAME> | smtp-password | keycloak-sync --provider <name>", 2), ct),
                "secrets:remove" => await run.FunctionSecretRemoveAsync(Arg(1) ?? throw new CliException("Usage: winche secrets:remove <NAME>", 2), ct),
                "secrets:sources" => await run.SecretSourcesAsync("sources", null, ct),
                "secrets:key" => await run.SecretSourcesAsync("key", Arg(1), ct),
                "secrets:test" => await run.SecretSourcesAsync("test", Arg(1), ct),
                "webhooks:secret" => await run.WebhookSecretAsync(Arg(1), ct),
                "server settings pull" or "server settings diff" or "server settings deploy" => await run.ServerSettingsAsync(Arg(2)!, ct),
                "server secrets" => await run.ServerSecretsAsync(null, ct),
                "server secrets:set" => await run.ServerSecretsAsync(Arg(2) ?? throw new CliException("Usage: winche server secrets:set <name>", 2), ct),
                "server users" => await run.ServerUsersAsync(null, null, ct),
                "server users:disable" => await run.ServerUsersAsync("disable", Arg(2), ct),
                "server users:enable" => await run.ServerUsersAsync("enable", Arg(2), ct),
                "server users:sign-out" => await run.ServerUsersAsync("sign-out", Arg(2), ct),
                "users" => await run.AppUsersAsync(null, null, ct),
                "users:disable" => await run.AppUsersAsync("disable", Arg(1), ct),
                "users:enable" => await run.AppUsersAsync("enable", Arg(1), ct),
                "users:sign-out" => await run.AppUsersAsync("sign-out", Arg(1), ct),
                "storage:move" => await run.StorageMoveAsync(ct),
                "data:move" => await run.DataMoveAsync(ct),
                "login:approve" => await run.LoginApproveAsync(Arg(1), ct),
                "logs" => await run.LogsAsync(ct),
                "products:start" => await run.ProductsStartAsync(a.Positional.Skip(1).ToList(), ct),
                "accounts" => await run.AccountsListAsync(ct),
                "accounts:create" => await run.AccountsCreateAsync(Arg(1) ?? throw new CliException("Usage: winche accounts:create <name> --role viewer|member|admin | --scopes functions,rules,indexes,assets", 2), ct),
                "accounts:keys:add" => await run.AccountsKeysAddAsync(Arg(1) ?? throw new CliException("Usage: winche accounts:keys:add <account> [--days 90]", 2), ct),
                "accounts:keys:revoke" => await run.AccountsKeysRevokeAsync(Arg(1) ?? throw new CliException("Usage: winche accounts:keys:revoke <account> <key id>", 2),
                    Arg(2) ?? throw new CliException("Usage: winche accounts:keys:revoke <account> <key id>", 2), ct),
                "accounts:trust" => await run.AccountsTrustAsync(Arg(1) ?? throw new CliException("Usage: winche accounts:trust <account> --issuer <url> --subject <pattern> --audience <aud>", 2), ct),
                "accounts:untrust" => await run.AccountsUntrustAsync(Arg(1) ?? throw new CliException("Usage: winche accounts:untrust <account> <identity id>", 2),
                    Arg(2) ?? throw new CliException("Usage: winche accounts:untrust <account> <identity id>", 2), ct),
                "accounts:disable" => await run.AccountsSwitchAsync(Arg(1) ?? throw new CliException("Usage: winche accounts:disable <account>", 2), true, ct),
                "accounts:enable" => await run.AccountsSwitchAsync(Arg(1) ?? throw new CliException("Usage: winche accounts:enable <account>", 2), false, ct),
                "accounts:delete" => await run.AccountsDeleteAsync(Arg(1) ?? throw new CliException("Usage: winche accounts:delete <account>", 2), ct),
                "tokens:list" or "tokens:create" or "tokens:revoke" => throw new CliException("CI tokens became service accounts: winche accounts (accounts:create, accounts:keys:add, accounts:trust).", 2),
                "settings pull" => await run.SettingsPullAsync(ct),
                "settings diff" => await run.SettingsDiffAsync(ct),
                "settings deploy" => await run.SettingsDeployAsync(ct),
                "indexes deploy" => await run.IndexesDeployAsync(ct),
                "indexes diff" => await run.IndexesDiffAsync(ct),
                "indexes pull" => await run.IndexesPullAsync(ct),
                "functions init" => run.FunctionsInit(a.Positional.ElementAtOrDefault(2)),
                "functions deploy" => await run.FunctionsDeployAsync(a.Positional.ElementAtOrDefault(2), ct),
                "plugins" => await run.PluginsListAsync(ct),
                "plugins:info" => await run.PluginInfoAsync(Arg(1) ?? throw new CliException("Usage: winche plugins:info <package>[@version]", 2), ct),
                "plugins:install" => await run.PluginInstallAsync(Arg(1) ?? throw new CliException("Usage: winche plugins:install <package>[@version] --id <id> [--set KEY=value …]", 2), ct),
                "plugins:configure" => await run.PluginConfigureAsync(Arg(1) ?? throw new CliException("Usage: winche plugins:configure <id> --set KEY=value …", 2), ct),
                "plugins:upgrade" => await run.PluginUpgradeAsync(Arg(1) ?? throw new CliException("Usage: winche plugins:upgrade <id>[@version] [--set KEY=value …]", 2), ct),
                "plugins:uninstall" => await run.PluginUninstallAsync(Arg(1) ?? throw new CliException("Usage: winche plugins:uninstall <id> [--remove-secrets]", 2), ct),
                "functions secrets" => await run.FunctionSecretsAsync(ct),
                "functions secrets:set" => await run.FunctionSecretSetAsync(Arg(2) ?? throw new CliException("Usage: winche secrets:set <NAME> [--from-file <path>]", 2), ct),
                "functions secrets:remove" => await run.FunctionSecretRemoveAsync(Arg(2) ?? throw new CliException("Usage: winche secrets:remove <NAME>", 2), ct),
                "pages build" => await run.PagesBuildAsync(ct),
                "assets init" => run.AssetsInit(Arg(2)),
                "assets deploy" => await run.AssetsDeployAsync(Arg(2), ct),
                "assets diff" => await run.AssetsDiffAsync(Arg(2), ct),
                "assets releases" => await run.AssetsReleasesAsync(ct),
                "assets release" => await run.AssetsReleaseAsync(Arg(2) ?? throw new CliException("Usage: winche assets release <number>", 2), ct),
                "assets channels" => await run.AssetsChannelsAsync(ct),
                "assets channels:set" => await run.AssetsChannelSetAsync(Arg(2), Arg(3), ct),
                "assets channels:remove" => await run.AssetsChannelRemoveAsync(Arg(2) ?? throw new CliException("Usage: winche assets channels:remove <name>", 2), ct),
                "emulators start" => await run.EmulatorsAsync(null, ct),
                "emulators exec" => await run.EmulatorsAsync(a.Positional.Count > 2 ? string.Join(' ', a.Positional.Skip(2)) : throw new CliException("Usage: winche emulators exec \"<command>\"", 2), ct),
                "emulators token" => run.Token(a.Positional.Skip(2).ToList()),
                "emulators idtoken" => run.IdToken(a.Positional.Skip(2).ToList()),
                "lsp" => await new LanguageServer(System.Console.OpenStandardInput(), System.Console.OpenStandardOutput(), env, http).RunAsync(ct),
                _ => throw new CliException($"Unknown command '{string.Join(' ', a.Positional)}'. Run: winche --help", 2),
            };
        }
        catch (CliException ex)
        {
            env.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }
        catch (OperationCanceledException)
        {
            env.Error.WriteLine("Cancelled.");
            return 130;
        }
    }

    internal sealed partial class Run(CliEnvironment env, Args args, HttpClient http)
    {
        private ProjectConfig? _config;
        private bool _configLoaded;

        private ProjectConfig? Config
        {
            get
            {
                if (!_configLoaded) { _config = ProjectConfig.Find(env.WorkingDirectory, args.Value("config")); _configLoaded = true; }
                return _config;
            }
        }

        private ProjectConfig RequireConfig() =>
            Config ?? throw new CliException($"No {ProjectConfig.FileName} here or in a parent directory. Run: winche init");

        private ConsoleTarget Target() =>
            ConsoleTarget.Resolve(Config, args.Value("console"), args.Value("project"), env)
            ?? throw new CliException($"Which project? Pass --console https://host --project <id>, set WINCHE_CONSOLE, or add \"server\" and \"project\" (or \"projects\": winche use --add <alias> https://host/projects/<id>) to {ProjectConfig.FileName}.");

        private Uri ConsoleUrl() => Target().Url;

        // ----- use --------------------------------------------------------------------------------

        public int Use(string? alias, string? url)
        {
            var config = RequireConfig();
            var path = config.FilePath!;
            if (args.Value("add") is { } add)
            {
                var target = url ?? alias ?? throw new CliException("Usage: winche use --add <alias> <console url>", 2);
                ConsoleTarget.Parse(target);
                (config.Projects ??= new(StringComparer.Ordinal))[add] = target.TrimEnd('/');
                config.Save(path);
                ActiveProjects.Set(env, path, add);
                env.Out.WriteLine($"Added {add} ({target.TrimEnd('/')}) and using it here.");
                return 0;
            }
            if (args.Value("remove") is { } remove)
            {
                if (config.Projects is null || !config.Projects.Remove(remove)) throw new CliException($"No project named '{remove}'.");
                if (config.Projects.Count == 0) config.Projects = null;
                config.Save(path);
                if (ActiveProjects.Get(env, path) == remove) ActiveProjects.Set(env, path, null);
                env.Out.WriteLine($"Removed {remove}.");
                return 0;
            }
            if (args.Flag("clear"))
            {
                ActiveProjects.Set(env, path, null);
                env.Out.WriteLine("No project picked here now.");
                return 0;
            }

            var projects = config.Projects ?? [];
            if (alias is null)
            {
                if (projects.Count == 0)
                {
                    env.Out.WriteLine($"No projects in {ProjectConfig.FileName}. Add one: winche use --add <alias> <console url>");
                    return 0;
                }
                var active = ActiveProjects.Get(env, path);
                foreach (var (name, target) in projects.OrderBy(p => p.Key, StringComparer.Ordinal))
                    env.Out.WriteLine($"{(name == active ? "*" : " ")} {name,-12} {target}");
                if (active is null) env.Out.WriteLine(projects.Count == 1 ? "(the only project is used)" : "(none picked: winche use <alias>)");
                return 0;
            }
            if (!projects.ContainsKey(alias))
                throw new CliException(projects.Count == 0
                    ? $"No projects in {ProjectConfig.FileName}. Add one: winche use --add {alias} <console url>"
                    : $"No project named '{alias}' (have {string.Join(", ", projects.Keys)}). Add it: winche use --add {alias} <console url>");
            ActiveProjects.Set(env, path, alias);
            env.Out.WriteLine($"Now using {alias} ({projects[alias]}) here.");
            return 0;
        }

        // ----- init ------------------------------------------------------------------------------

        public int Init()
        {
            var path = Path.Combine(env.WorkingDirectory, ProjectConfig.FileName);
            if (File.Exists(path)) throw new CliException($"{ProjectConfig.FileName} already exists here.");

            // --console https://host[/projects/<id>] (and --project <id>): the server and project in winche.json.
            var at = args.Value("console") is { } given ? ConsoleTarget.Parse(given) : null;
            var config = new ProjectConfig
            {
                Server = at is null ? null : ConsoleAddress.RootOf(at).ToString().TrimEnd('/'),
                Project = args.Value("project") ?? (at is null ? null : ConsoleAddress.ProjectOf(at)),
            };
            // Database and storage unless --only names others (assets: winche assets init).
            foreach (var sys in args.Value("only") is null ? ["database", "storage"] : Selected(ProjectConfig.Subsystems))
            {
                var file = $"{sys}.rules";
                var tests = $"{sys}.rules.test";
                config.Rules[sys] = file;
                (config.Tests ??= new(StringComparer.Ordinal))[sys] = tests;
                foreach (var (name, content) in new[] { (file, Starter(sys)), (tests, StarterTests(sys)) })
                {
                    var full = Path.Combine(env.WorkingDirectory, name);
                    if (File.Exists(full)) { env.Out.WriteLine($"  kept    {name}"); continue; }
                    File.WriteAllText(full, content);
                    env.Out.WriteLine($"  created {name}");
                }
            }
            config.Save(path);
            env.Out.WriteLine($"  created {ProjectConfig.FileName}");
            env.Out.WriteLine();
            env.Out.WriteLine(config.Server is null || config.Project is null
                ? $"Next: add \"server\" and \"project\" to {ProjectConfig.FileName}, run winche login, then winche rules deploy."
                : "Next: winche login, then winche rules deploy.");
            return 0;
        }

        private static string Starter(string sys) => sys == "assets"
            ? """
              // Who may read which assets (apps only read them; releases are published from the CLI). Nothing is
              // readable until a rule allows it. `list` decides what a channel's listing shows; `get`, the file itself.
              // resource: path, size, mimeType, hash, release, channel.
              //
              // match translations/{file=**} {
              //   allow read;
              // }
              // match staff/{file=**} {
              //   allow read: if request.auth.token.role == 'staff';
              // }

              """
            : sys == "database"
            ? """
              // Access rules for Winche.Database. Nothing is allowed until a rule grants it.
              // Check with `winche rules validate`; publish with `winche rules deploy`.
              //
              // match users/{userId} {
              //   allow read, write: if request.auth.uid == userId;
              // }

              """
            : """
              // Access rules for Winche.Storage. Nothing is allowed until a rule grants it.
              // Check with `winche rules validate`; publish with `winche rules deploy`.
              //
              // match userFiles/{userId}/{file=**} {
              //   allow read, write: if request.auth.uid == userId;
              // }

              """;

        private static string StarterTests(string sys) => sys == "assets"
            ? """
              // Checks for assets.rules, run by `winche rules test` and before `winche rules deploy`.

              test "nothing is readable without a rule" {
                deny get secret/notes.txt
              }

              """
            : sys == "database"
            ? """
              // Checks for database.rules, run by `winche rules test` and before `winche rules deploy`.
              //
              // test "owners read their profile" {
              //   auth { uid: 'u1' }                 // request.auth's claims; omit when signed out
              //   resource { name: 'Ann' }           // the existing document
              //   allow get users/u1
              //   deny get users/u2
              //   allow create users/u1 with { name: 'Ann' }
              //   allow list notes where ownerId == 'u1'
              // }

              test "signed out can't read users" {
                deny get users/u1
              }

              """
            : """
              // Checks for storage.rules, run by `winche rules test` and before `winche rules deploy`.

              test "signed out can't read files" {
                deny get userFiles/u1/photo.png
              }

              """;

        // ----- emulators -------------------------------------------------------------------------

        // ----- functions -------------------------------------------------------------------------

        private string FunctionsDir(string? given) => given is not null ? Path.GetFullPath(given, env.WorkingDirectory)
            : Config?.FunctionsPath ?? Path.Combine(Config?.Directory ?? env.WorkingDirectory, "functions");

        public async Task<int> PagesBuildAsync(CancellationToken ct)
        {
            var pages = PageBuilds.Of(Config);
            if (pages.Count == 0) { env.Out.WriteLine("There are no pages with a build in winche.json (\"pages\")."); return 0; }
            return await PageBuilds.BuildAsync(pages, env, ct) ? 0 : 1;
        }

        public int FunctionsInit(string? dir)
        {
            var target = FunctionsDir(dir);
            var made = FunctionsFolder.Init(target);
            foreach (var f in made) env.Out.WriteLine($"  wrote {Path.GetRelativePath(env.WorkingDirectory, Path.Combine(target, f))}");
            if (Config is { FilePath: { } file } config && config.Functions is null)
            {
                var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!.AsObject();
                json["functions"] = Path.GetRelativePath(config.Directory, target).Replace('\\', '/');
                File.WriteAllText(file, json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
                env.Out.WriteLine($"  added \"functions\" to {Path.GetFileName(file)}");
            }
            env.Out.WriteLine("Functions are ready: edit index.ts (the Deno extension for VS Code gives types), then run: winche emulators start");
            return 0;
        }

        // ----- indexes -----------------------------------------------------------------------------

        private const string IndexesApi = "settings/database/indexes";

        private string IndexesFile()
        {
            var path = RequireConfig().IndexesPath;
            if (!File.Exists(path)) throw new CliException($"No {Path.GetRelativePath(env.WorkingDirectory, path)}. Run: winche indexes pull");
            return path;
        }

        private async Task<string> LiveIndexesAsync(ConsoleClient client, CancellationToken ct) =>
            (await client.GetJsonAsync(IndexesApi, ct))["text"]?.GetValue<string>() ?? "";

        /// <summary>Checks the file on the console, then saves it there (the note is the reason in the history).</summary>
        public async Task<int> IndexesDeployAsync(CancellationToken ct)
        {
            var path = IndexesFile();
            var text = await File.ReadAllTextAsync(path, ct);
            var client = await ClientAsync(ct);
            var (_, parsed) = await client.PostAsync($"{IndexesApi}/parse", new { text }, "indexes", ct);
            if (parsed["ok"]?.GetValue<bool>() != true && parsed["error"] is System.Text.Json.Nodes.JsonObject e)
                throw new CliException($"{Path.GetRelativePath(env.WorkingDirectory, path)}:{e["line"]}:{e["column"]}: {e["message"]}");
            var live = await LiveIndexesAsync(client, ct);
            if (Normalize(live) == Normalize(await FormattedAsync(client, text, ct)))
            {
                env.Out.WriteLine("indexes: no changes.");
                return 0;
            }
            var note = args.Value("message") ?? DefaultNote().Replace("winche rules deploy", "winche indexes deploy");
            var (ok, saved) = await client.PutAsync(IndexesApi, new { text, reason = note }, "indexes", ct);
            if (!ok) throw new CliException($"indexes: the console refused them: {saved["error"]?["message"] ?? saved["error"] ?? saved}");
            env.Out.WriteLine($"indexes: deployed as version {saved["number"]} ({note}). Indexes are built in the background.");
            return 0;
        }

        // What the console would show for this text (its own formatting), to compare without whitespace and comments.
        private static async Task<string> FormattedAsync(ConsoleClient client, string text, CancellationToken ct)
        {
            var (_, parsed) = await client.PostAsync($"{IndexesApi}/parse", new { text }, "indexes", ct);
            if (parsed["indexes"] is null) return text;
            var indexes = System.Text.Json.JsonSerializer.Deserialize<List<Winche.Database.Settings.IndexSettings>>(parsed["indexes"]!.ToJsonString(), Web)!;
            var ttl = System.Text.Json.JsonSerializer.Deserialize<List<Winche.Database.Settings.TtlSettings>>(parsed["ttl"]!.ToJsonString(), Web)!;
            return Winche.Database.Settings.IndexText.Format(indexes, ttl);
        }

        private static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);
        private static string Normalize(string text) => text.Replace("\r\n", "\n").Trim();

        /// <summary>What deploying the file would change on the console.</summary>
        public async Task<int> IndexesDiffAsync(CancellationToken ct)
        {
            var path = IndexesFile();
            var client = await ClientAsync(ct);
            var live = await LiveIndexesAsync(client, ct);
            var mine = await FormattedAsync(client, await File.ReadAllTextAsync(path, ct), ct);
            if (Normalize(live) == Normalize(mine)) { env.Out.WriteLine("indexes: no changes."); return 0; }
            WriteDiff(live, mine, "console", Path.GetRelativePath(env.WorkingDirectory, path));
            return 0;
        }

        /// <summary>Writes the console's indexes and expiry to the file (or stdout).</summary>
        public async Task<int> IndexesPullAsync(CancellationToken ct)
        {
            var client = await ClientAsync(ct);
            var live = await LiveIndexesAsync(client, ct);
            if (args.Flag("stdout")) { env.Out.Write(live); return 0; }
            var path = RequireConfig().IndexesPath;
            await File.WriteAllTextAsync(path, live, ct);
            env.Out.WriteLine($"indexes: wrote {Path.GetRelativePath(env.WorkingDirectory, path)} (comments aren't kept on the console)");
            return 0;
        }

        // ----- projects ----------------------------------------------------------------------------

        /// <summary>The projects on the console's Winche Server that this account may use (and the archived, for server admins).</summary>
        public async Task<int> ProjectsAsync(CancellationToken ct)
        {
            // The server's list: --url, this project's server, or the one server you're signed in to.
            var console = ServerConsole();
            var body = await (await ClientAsync(console, ct)).GetJsonAsync("projects", ct);
            if (body["available"]?.GetValue<bool>() != true)
            {
                env.Out.WriteLine($"{ConsoleAddress.Describe(console)} runs one project: {body["current"]}.");
                return 0;
            }
            var current = body["current"]?.GetValue<string>();
            var listed = body["projects"]!.AsArray();
            if (listed.Count == 0 && body["archived"]?.AsArray() is not { Count: > 0 })
            {
                env.Out.WriteLine($"{ConsoleAddress.Describe(console)} has no projects yet. Make one (server admins): winche projects:create <id> --name \"Its name\"");
                return 0;
            }
            foreach (var p in listed)
            {
                var id = p!["id"]!.GetValue<string>();
                var url = ConsoleAddress.Describe(ConsoleTarget.ForProject(console, id));
                env.Out.WriteLine($"{(id == current ? "*" : " ")} {id,-24} {p["name"]}  {url}");
            }
            if (body["archived"]?.AsArray() is { Count: > 0 } archived)
            {
                env.Out.WriteLine("Archived:");
                foreach (var a in archived) env.Out.WriteLine($"  {a!["id"],-24} {a["name"]}");
            }
            env.Out.WriteLine("Use one: --project <id> (or winche use --add <alias> <its address>).");
            return 0;
        }

        // ----- configure ---------------------------------------------------------------------------

        /// <summary>
        /// Reads the server's /.well-known/winche and writes lib/winche_options.dart: a DefaultWincheOptions per
        /// platform, for WincheFlutter.initializeApp(options: DefaultWincheOptions.currentPlatform).
        /// </summary>
        public async Task<int> ConfigureAsync(CancellationToken ct)
        {
            var url = (args.Value("url") ?? env.GetVariable("WINCHE_SERVER") ?? $"http://localhost:{args.Value("port") ?? "9090"}").TrimEnd('/');
            // --project <id>: that project on a Winche Server (its address, /projects/<id>).
            if (args.Value("project") is { } projectId && args.Value("url") is not null)
            {
                if (!WincheIds.IsValid(projectId)) throw new CliException($"--project {projectId} isn't a project id.", 2);
                url = $"{url}/projects/{projectId}";
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var server) || server.Scheme is not ("http" or "https"))
                throw new CliException($"{url} isn't an address. Pass --url https://api.example.com", 2);

            WincheAppSetup setup;
            try
            {
                var appId = args.Value("app");
                using var response = await http.GetAsync(new Uri(server.ToString().TrimEnd('/') + "/.well-known/winche" + (appId is null ? "" : $"?app={Uri.EscapeDataString(appId)}")), ct);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    if (appId is not null && body.Contains("\"error\"", StringComparison.Ordinal))
                        throw new CliException($"No app {appId} is registered there. Register it in the console (Project settings → Your apps) and copy its id.", 2);
                    throw new CliException($"{url} doesn't publish its setup (/.well-known/winche). Is it a Winche Server or the emulators, up to date?");
                }
                response.EnsureSuccessStatusCode();
                setup = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<WincheAppSetup>(
                    response.Content, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web), ct)
                    ?? throw new CliException($"{url} sent an empty setup.");
            }
            catch (HttpRequestException ex)
            {
                throw new CliException(args.Value("url") is null && env.GetVariable("WINCHE_SERVER") is null
                    ? $"Couldn't reach the emulators at {url} ({ex.Message}). Start them (winche emulators start) or pass --url."
                    : $"Couldn't reach {url}: {ex.Message}");
            }

            var platforms = args.Value("platforms") is { } list
                ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : WincheAppSetup.AllPlatforms.Where(p => Directory.Exists(Path.Combine(env.WorkingDirectory, p))).ToArray();
            var unknown = platforms.Where(p => !WincheAppSetup.AllPlatforms.Contains(p.ToLowerInvariant())).ToList();
            if (unknown.Count > 0)
                throw new CliException($"Unknown platform {string.Join(", ", unknown)}: use {string.Join(", ", WincheAppSetup.AllPlatforms)}.", 2);
            if (platforms.Length == 0) platforms = [.. WincheAppSetup.AllPlatforms];

            var target = Path.GetFullPath(args.Value("out") ?? Path.Combine("lib", "winche_options.dart"), env.WorkingDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, setup.DartOptionsFile(platforms), ct);

            env.Out.WriteLine($"Wrote {Path.GetRelativePath(env.WorkingDirectory, target)} for project {setup.ProjectId} at {setup.BaseUrl}");
            env.Out.WriteLine(setup.App is { } registered
                ? $"  app:       {registered.Name} ({registered.Id})"
                : "  app:       none (add it: winche apps:create <name>, then run this again with --app <its id>)");
            env.Out.WriteLine($"  platforms: {string.Join(", ", platforms)}");
            env.Out.WriteLine($"  services:  {(setup.Services.Count == 0 ? "none" : string.Join(", ", setup.Services.Select(s => s.Id)))}");
            if (setup.Providers.Where(p => p.ClientIds.Count > 1).Select(p => p.Name).ToList() is { Count: > 0 } several)
                env.Out.WriteLine($"  {string.Join(", ", several)}: several client ids; the file lists them for you to pick one per platform.");
            if (!File.Exists(Path.Combine(env.WorkingDirectory, "pubspec.yaml")))
                env.Out.WriteLine("  (no pubspec.yaml here: run this in your app's folder, or pass --out)");
            env.Out.WriteLine("Next: flutter pub add winche_flutter, then in main():");
            env.Out.WriteLine("  final app = await WincheFlutter.initializeApp(options: DefaultWincheOptions.currentPlatform);");
            return 0;
        }

        public async Task<int> FunctionsDeployAsync(string? dir, CancellationToken ct)
        {
            var folder = FunctionsDir(dir);
            var client = await ClientAsync(ct);
            var pages = PageBuilds.Of(Config);
            if (!args.Flag("no-build") && !await PageBuilds.BuildAsync(pages, env, ct)) return 1;
            return await FunctionsFolder.DeployAsync(client, folder, args.Value("message"), env, ct, pages,
                Config?.Plugins is { } listed ? await PluginsForDeployAsync(client, listed, ct) : null);
        }

        public async Task<int> EmulatorsAsync(string? command, CancellationToken ct)
        {
            var port = 9090;
            if (args.Value("port") is { } p && (!int.TryParse(p, out port) || port is < 0 or > 65535))
                throw new CliException($"--port {p} isn't a port number.", 2);
            var config = Config;
            string? Rules(string sys) => config is not null && config.Rules.ContainsKey(sys) ? config.RulesPath(sys) : null;
            var options = new Emulator.EmulatorOptions
            {
                Port = port,
                Host = args.Value("host") ?? "localhost",
                ConnectionString = args.Value("pg"),
                DataDirectory = args.Value("data") is { } d ? Path.GetFullPath(d, env.WorkingDirectory) : null,
                DatabaseRules = Rules("database"),
                StorageRules = Rules("storage"),
                AssetsRules = Rules("assets"),
                Assets = AssetsDir(null) is var adir && Directory.Exists(adir) ? adir : null,
                AssetsChannel = AssetsChannel(),
                Functions = FunctionsDir(null) is var fdir && Directory.Exists(fdir) ? fdir : null,
                Pages = PageBuilds.Of(config),
                Plugins = config?.Plugins,
                Quiet = args.Flag("quiet") || command is not null,
                Verbose = args.Flag("verbose"),
                ProjectId = config?.ProjectId ?? "demo",
            };

            if (options.Functions is not null) options = options with { DenoPath = await DenoInstaller.EnsureAsync(env, http, ct) };
            await using var host = await Emulator.EmulatorHost.StartAsync(options, env.Out, ct);
            if (command is null)
            {
                host.PrintBanner(options.DatabaseRules, options.StorageRules, options.AssetsRules);
                env.Out.WriteLine("Press Ctrl+C to stop.");
                using var either = CancellationTokenSource.CreateLinkedTokenSource(ct, host.Stopping);
                try { await Task.Delay(Timeout.Infinite, either.Token); }
                catch (OperationCanceledException) { env.Out.WriteLine("Stopping…"); }
                return 0;
            }

            env.Out.WriteLine($"Emulators at {host.ProjectUrl}; running: {command}");
            var psi = OperatingSystem.IsWindows()
                ? new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", command])
                : new System.Diagnostics.ProcessStartInfo("/bin/sh", ["-c", command]);
            psi.WorkingDirectory = env.WorkingDirectory;
            psi.Environment["WINCHE_EMULATOR"] = host.BaseUrl;
            psi.Environment["WINCHE_EMULATOR_HOST"] = new Uri(host.BaseUrl).Authority;
            psi.Environment["WINCHE_DATABASE_URL"] = host.BaseUrl + "/documents/v1";
            psi.Environment["WINCHE_STORAGE_URL"] = host.BaseUrl + "/files/v1";
            psi.Environment["WINCHE_ASSETS_URL"] = host.BaseUrl + "/assets/v1";
            using var process = System.Diagnostics.Process.Start(psi) ?? throw new CliException($"Couldn't run: {command}");
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, host.Stopping);
            try
            {
                await process.WaitForExitAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            env.Out.WriteLine($"{command} exited with {process.ExitCode}; stopping the emulators.");
            return process.ExitCode;
        }

        public int Token(List<string> words)
        {
            if (words.Count == 0) throw new CliException("Usage: winche emulators token <uid> [claim=value …]", 2);
            var claims = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var pair in words.Skip(1))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) throw new CliException($"'{pair}' isn't claim=value.", 2);
                var value = pair[(eq + 1)..];
                claims[pair[..eq]] = value switch
                {
                    "true" => true,
                    "false" => false,
                    _ when long.TryParse(value, out var l) => l,
                    _ => value,
                };
            }
            env.Out.WriteLine(Emulator.TestTokens.Mint(words[0], claims));
            return 0;
        }

        public int IdToken(List<string> words)
        {
            if (words.Count != 2 || words[0] is not ("google" or "apple") || !words[1].Contains('@'))
                throw new CliException("Usage: winche emulators idtoken <google|apple> <email>", 2);
            env.Out.WriteLine(Emulator.TestTokens.MintIdToken(words[0], words[1]));
            return 0;
        }

        // ----- login / logout --------------------------------------------------------------------

        /// <summary>
        /// <c>winche login</c>: signs in to the server (once for all its projects). Already signed in, it says so, unless
        /// <c>--reauth</c>. <c>winche login:add</c> signs in another account beside it (<c>login:use</c> switches).
        /// </summary>
        public async Task<int> LoginAsync(bool add, CancellationToken ct)
        {
            var console = ServerConsole();
            var server = CredentialStore.ServerOf(console);
            var store = new CredentialStore(env.ConfigDirectory);
            // Signed in already, as long as the server still has the session (renewing it says so): else sign in again.
            if (!add && !args.Flag("reauth") && store.Active(console) is { } signedIn
                && await new ConsoleLogin(env, http).StillLiveAsync(server, signedIn, ct) is { } live)
            {
                if (!ReferenceEquals(live, signedIn)) store.Put(console, live);
                env.Out.WriteLine($"Already signed in to {CredentialStore.Key(console)} as {signedIn.User}.");
                env.Out.WriteLine("winche login --reauth signs in again; winche login:add adds another account.");
                return 0;
            }
            var port = ConsoleLogin.DefaultPort;
            if (args.Value("port") is { } p && (!int.TryParse(p, out port) || port is < 1 or > 65535))
                throw new CliException($"--port {p} isn't a port number.", 2);

            var login = args.Flag("no-browser")
                ? await new ConsoleLogin(env, http).DeviceLoginAsync(server, ct)
                : await new ConsoleLogin(env, http).LoginAsync(server, port, ct);
            var use = !add || store.Active(console) is null;
            var replaced = store.Server(console)?.Accounts.GetValueOrDefault(login.User);
            store.Put(console, login, use);
            if (replaced is not null) await new ConsoleLogin(env, http).LogoutAsync(server, replaced, ct);   // --reauth: the old session ends
            env.Out.WriteLine($"Signed in to {CredentialStore.Key(console)} as {login.User}{(use ? "" : $" (still using {store.Active(console)?.User}: winche login:use {login.User})")}.");
            return 0;
        }

        /// <summary><c>winche login:list</c>: every server's signed-in accounts, the one in use marked.</summary>
        public int LoginList()
        {
            var all = new CredentialStore(env.ConfigDirectory).All();
            if (all.Count == 0)
            {
                env.Out.WriteLine("Not signed in anywhere. Run: winche login");
                return 0;
            }
            foreach (var (server, logins) in all.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                env.Out.WriteLine(server);
                foreach (var user in logins.Accounts.Keys.Order(StringComparer.OrdinalIgnoreCase))
                    env.Out.WriteLine($"  {(string.Equals(user, logins.Active, StringComparison.OrdinalIgnoreCase) ? "*" : " ")} {user}");
            }
            return 0;
        }

        /// <summary><c>winche login:use &lt;email&gt;</c>: the account commands use on this server.</summary>
        public int LoginUse(string user)
        {
            var console = ServerConsole();
            if (!new CredentialStore(env.ConfigDirectory).Use(console, user))
                throw new CliException($"{user} isn't signed in to {CredentialStore.Key(console)}. Run: winche login:add");
            env.Out.WriteLine($"Using {user} on {CredentialStore.Key(console)}.");
            return 0;
        }

        /// <summary><c>winche logout [&lt;email&gt;]</c>: ends one account's sign-in there, or (none named) every one.</summary>
        public async Task<int> LogoutAsync(string? user, CancellationToken ct)
        {
            var console = ServerConsole();
            var server = CredentialStore.ServerOf(console);
            var store = new CredentialStore(env.ConfigDirectory);
            var signedIn = store.Server(console)?.Accounts ?? [];
            var leaving = user is null ? signedIn.Keys.ToList() : signedIn.Keys.Where(u => string.Equals(u, user, StringComparison.OrdinalIgnoreCase)).ToList();
            if (leaving.Count == 0)
            {
                env.Out.WriteLine(user is null ? $"Not signed in to {CredentialStore.Key(console)}." : $"{user} isn't signed in to {CredentialStore.Key(console)}.");
                return 0;
            }
            foreach (var u in leaving)
            {
                await new ConsoleLogin(env, http).LogoutAsync(server, signedIn[u], ct);   // ends the session there too
                store.Remove(console, u);
                env.Out.WriteLine($"Signed {u} out of {CredentialStore.Key(console)}.");
            }
            if (store.Active(console) is { } next && leaving.Count < signedIn.Count)
            {
                env.Out.WriteLine($"Now using {next.User} there (winche login:use to change).");
            }
            return 0;
        }

        private async Task<ConsoleClient> ClientAsync(CancellationToken ct) => await ClientAsync(null, ct);

        /// <summary>
        /// A client for <paramref name="url"/> (default: the project's console): as a service account (WINCHE_TOKEN or
        /// --token: a key, exchanged for an access token, or an access token; or WINCHE_ID_TOKEN: the CI job's own token,
        /// for an account that trusts its identity), else as the signed-in account.
        /// </summary>
        private async Task<ConsoleClient> ClientAsync(Uri? url, CancellationToken ct)
        {
            Uri console;
            if (url is not null) console = url;
            else
            {
                var target = Target();
                console = target.Url;
                if (target.Alias is not null && !args.Flag("quiet")) env.Out.WriteLine($"Project: {target.Describe()}");
            }
            var token = await ServiceAccountTokenAsync(console, ct)
                        ?? await SignedInTokenAsync(env, http, console, ct)
                        ?? throw new CliException($"Not signed in to {CredentialStore.Key(console)}. Run: winche login");
            return new ConsoleClient(http, console, token);
        }

        /// <summary>The service account's access token, from WINCHE_TOKEN / --token or WINCHE_ID_TOKEN; null without either.</summary>
        private async Task<string?> ServiceAccountTokenAsync(Uri console, CancellationToken ct)
        {
            var token = (args.Value("token") ?? env.GetVariable("WINCHE_TOKEN"))?.Trim();
            if (token is not null && token.Any(char.IsWhiteSpace))
                throw new CliException("The token has spaces or more than one line in it: set WINCHE_TOKEN (or --token) to the token alone.", 2);
            if (!string.IsNullOrEmpty(token))
                return ServiceAccounts.IsKey(token) ? await ServiceAccounts.ExchangeAsync(http, console, new { key = token }, ct) : token;
            if (env.GetVariable("WINCHE_ID_TOKEN")?.Trim() is { Length: > 0 } identity)
                return await ServiceAccounts.ExchangeAsync(http, console, new { assertion = identity }, ct);
            return null;
        }

        /// <summary>
        /// The active account's access token for <paramref name="console"/>'s server, renewed when it's about to run out;
        /// null when nobody's signed in there. A session that has ended is forgotten, with a hint to sign in again.
        /// </summary>
        internal static async Task<string?> SignedInTokenAsync(CliEnvironment env, HttpClient http, Uri console, CancellationToken ct)
        {
            var store = new CredentialStore(env.ConfigDirectory);
            if (store.Active(console) is not { } saved) return null;
            var fresh = await new ConsoleLogin(env, http).FreshAsync(CredentialStore.ServerOf(console), saved, ct);
            if (fresh is null)
            {
                store.Remove(console, saved.User, chooseNext: false);   // never carry on as someone else unasked
                var others = store.Server(console)?.Accounts.Keys.ToList() ?? [];
                throw new CliException($"Your sign-in to {CredentialStore.Key(console)} as {saved.User} has ended. Run: winche login" +
                    (others.Count > 0 ? $" (or winche login:use {others[0]})" : ""));
            }
            if (!ReferenceEquals(fresh, saved)) store.Put(console, fresh);
            return fresh.AccessToken;
        }

        // ----- rules -----------------------------------------------------------------------------

        private IReadOnlyList<string> Selected(IEnumerable<string> available)
        {
            var list = available.ToList();
            if (args.Value("only") is not { } only) return list;
            var wanted = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var w in wanted)
                if (!ProjectConfig.Subsystems.Contains(w))
                    throw new CliException($"--only {w}: expected database, storage or assets.", 2);
            var missing = wanted.Where(w => !list.Contains(w)).ToList();
            if (missing.Count > 0)
                throw new CliException($"{string.Join(", ", missing)} has no rules file in {ProjectConfig.FileName}.");
            return [.. list.Where(wanted.Contains)];
        }

        private IReadOnlyList<string> SelectedFiles()
        {
            var config = RequireConfig();
            var selected = Selected(ProjectConfig.Subsystems.Where(config.Rules.ContainsKey));
            if (selected.Count == 0)
                throw new CliException($"{config.FilePath} lists no rules files (\"rules\": {{ \"database\": \"database.rules\" }}).");
            return selected;
        }

        private sealed record LocalRules(string Subsystem, string Path, string Display, RuleSet RuleSet);

        /// <summary>Parses every selected file; reports all problems, then fails if any.</summary>
        private List<LocalRules> LoadAll()
        {
            var config = RequireConfig();
            var result = new List<LocalRules>();
            var failed = false;
            foreach (var sys in SelectedFiles())
            {
                var path = config.RulesPath(sys);
                var display = System.IO.Path.GetRelativePath(env.WorkingDirectory, path);
                if (!File.Exists(path))
                {
                    env.Error.WriteLine($"✗ {display}: file not found ({sys} rules in {ProjectConfig.FileName}).");
                    failed = true;
                    continue;
                }

                RuleSet ruleSet;
                try
                {
                    ruleSet = RuleText.ParseRuleSet(File.ReadAllText(path));
                }
                catch (RuleSyntaxException ex)
                {
                    env.Error.WriteLine($"✗ {display}:{ex.Line}:{ex.Column}: {ex.Reason}");
                    failed = true;
                    continue;
                }
                catch (ArgumentException ex)
                {
                    env.Error.WriteLine($"✗ {display}: {ex.Message}");
                    failed = true;
                    continue;
                }

                var issues = RuleSetValidation.Validate(ruleSet);
                foreach (var issue in issues)
                    env.Error.WriteLine($"✗ {display}: {(issue.Path is null ? "" : $"match {issue.Path}: ")}{issue.Message}");
                if (issues.Count > 0) { failed = true; continue; }

                foreach (var name in UnknownFunctions(ruleSet))
                    env.Error.WriteLine($"! {display}: unknown function '{name}' — it will always deny.");
                result.Add(new(sys, path, display, ruleSet));
            }
            if (failed) throw new CliException("Fix the rules above first.");
            return result;
        }

        public int Validate()
        {
            foreach (var local in LoadAll())
                env.Out.WriteLine($"✓ {local.Display} ({local.Subsystem}, {Count(local.RuleSet)})");
            return 0;
        }

        public async Task<int> TestAsync(CancellationToken ct)
        {
            var locals = LoadAll();
            var config = RequireConfig();
            if (!locals.Any(l => config.TestsPath(l.Subsystem) is not null))
                throw new CliException($"No test files: add \"tests\": {{ \"database\": \"database.rules.test\" }} to {ProjectConfig.FileName}.");
            return await RunTestsAsync(locals, verbose: true, ct) ? 0 : 1;
        }

        /// <summary>Runs each file's cases; prints failures (and passes when <paramref name="verbose"/>).</summary>
        private async Task<bool> RunTestsAsync(List<LocalRules> locals, bool verbose, CancellationToken ct)
        {
            var config = RequireConfig();
            int passed = 0, failed = 0;
            foreach (var local in locals)
            {
                if (config.TestsPath(local.Subsystem) is not { } file) continue;
                var display = Path.GetRelativePath(env.WorkingDirectory, file);
                if (!File.Exists(file)) throw new CliException($"{display}: file not found ({local.Subsystem} tests in {ProjectConfig.FileName}).");

                IReadOnlyList<RuleTests.Outcome> outcomes;
                try
                {
                    outcomes = await RuleTests.RunAsync(local.Subsystem, local.RuleSet, file, ct);
                }
                catch (RuleTestSyntaxException ex)
                {
                    throw new CliException($"{display}:{ex.Line}:{ex.Column}: {ex.Message}");
                }

                env.Out.WriteLine($"{display} ({local.Subsystem})");
                foreach (var group in outcomes.GroupBy(o => o.Check.Test))
                {
                    var bad = group.Where(o => !o.Passed).ToList();
                    passed += group.Count() - bad.Count;
                    failed += bad.Count;
                    if (bad.Count == 0) { if (verbose) env.Out.WriteLine($"  ✓ {group.Key}"); continue; }
                    env.Out.WriteLine($"  ✗ {group.Key}");
                    foreach (var o in bad)
                        env.Out.WriteLine($"      {display}:{o.Check.Line}: {o.Check.Describe()} — got {(o.Allowed ? "allow" : "deny")}");
                }
                if (!verbose && failed == 0) env.Out.WriteLine($"  ✓ {outcomes.Count} checks passed");
            }
            env.Out.WriteLine(failed == 0 ? $"{passed} checks passed" : $"{passed} checks passed, {failed} failed");
            return failed == 0;
        }

        public async Task<int> DeployAsync(CancellationToken ct)
        {
            var locals = LoadAll();          // nothing is deployed unless every file is valid
            if (!args.Flag("skip-tests") && locals.Any(l => RequireConfig().TestsPath(l.Subsystem) is not null)
                && !await RunTestsAsync(locals, verbose: false, ct))
                throw new CliException("Tests failed; nothing was deployed (--skip-tests to deploy anyway).");
            var client = await ClientAsync(ct);
            var dryRun = args.Flag("dry-run");
            var note = args.Value("message") ?? DefaultNote();

            foreach (var local in locals)
            {
                var active = await client.GetActiveAsync(local.Subsystem, ct);
                var current = active is null ? null : RuleJson.DeserializeRuleSet(active.RulesJson);
                if (current is not null && current.Equals(local.RuleSet) && !args.Flag("force"))
                {
                    env.Out.WriteLine($"{local.Subsystem}: up to date (v{active!.Version})");
                    continue;
                }

                if (dryRun)
                {
                    env.Out.WriteLine($"{local.Subsystem}: would deploy {local.Display} as v{(active?.Version ?? 0) + 1}");
                    var before = current ?? RuleJson.DeserializeRuleSet(await client.GetLiveAsync(local.Subsystem, ct));
                    WriteDiff(RuleText.FormatRuleSet(before), RuleText.FormatRuleSet(local.RuleSet),
                        active is null ? "live (the app's starting rules)" : $"v{active.Version} (console)", local.Display);
                    continue;
                }

                var saved = await client.SaveAsync(local.Subsystem, RuleJson.Serialize(local.RuleSet), note, active?.Version, ct);
                env.Out.WriteLine($"{local.Subsystem}: deployed {local.Display} as v{saved.Version}");
            }
            return 0;
        }

        /// <summary>The subsystems for history/rollback: --only, else the configured files, else both.</summary>
        private IReadOnlyList<string> RemoteSubsystems()
        {
            var configured = Config is { } c ? ProjectConfig.Subsystems.Where(c.Rules.ContainsKey).ToList() : [];
            return Selected(configured.Count > 0 && args.Value("only") is null ? configured : ProjectConfig.Subsystems);
        }

        public async Task<int> HistoryAsync(string? versionArg, CancellationToken ct)
        {
            var client = await ClientAsync(ct);
            var subsystems = RemoteSubsystems();

            if (versionArg is not null)
            {
                var sys = Single(subsystems, "rules history <version>");
                var version = ParseVersion(versionArg);
                var chosen = await client.GetVersionAsync(sys, version, ct);
                var active = await client.GetActiveAsync(sys, ct);
                var text = RuleText.FormatRuleSet(RuleJson.DeserializeRuleSet(chosen.RulesJson));
                if (active is null || active.Version == version)
                {
                    env.Out.WriteLine($"{sys} v{version}{(active?.Version == version ? " (active)" : "")}:");
                    env.Out.WriteLine(text);
                    return 0;
                }
                env.Out.WriteLine($"{sys} v{version} against the active v{active.Version}:");
                WriteDiff(RuleText.FormatRuleSet(RuleJson.DeserializeRuleSet(active.RulesJson)), text, $"v{active.Version} (active)", $"v{version}");
                return 0;
            }

            foreach (var sys in subsystems)
            {
                var versions = await client.GetVersionsAsync(sys, ct);
                env.Out.WriteLine($"{sys}:");
                if (versions.Count == 0)
                {
                    env.Out.WriteLine("  no versions yet (the app's starting rules apply)");
                    continue;
                }
                foreach (var v in versions.OrderByDescending(v => v.Version))
                {
                    var note = v.Note ?? (v.RevertedFromVersion is { } from ? $"Reverted to v{from}" : "");
                    env.Out.WriteLine(
                        $"  {(v.IsActive ? "*" : " ")} v{v.Version,-4} {v.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {v.CreatedBy ?? "?",-24}  {note}".TrimEnd());
                }
            }
            env.Out.WriteLine("(* = active)");
            return 0;
        }

        public async Task<int> RollbackAsync(string? versionArg, CancellationToken ct)
        {
            if (versionArg is null) throw new CliException("Which version? Usage: winche rules rollback <version> [--only database|storage]", 2);
            var version = ParseVersion(versionArg);
            var client = await ClientAsync(ct);
            var sys = Single(RemoteSubsystems(), "rules rollback");

            var target = await client.GetVersionAsync(sys, version, ct);
            var active = await client.GetActiveAsync(sys, ct);
            if (active?.Version == version)
            {
                env.Out.WriteLine($"{sys}: v{version} is already active.");
                return 0;
            }

            env.Out.WriteLine($"{sys}: make v{version} active again{(active is null ? "" : $" (replacing v{active.Version})")}:");
            WriteDiff(
                active is null ? "" : RuleText.FormatRuleSet(RuleJson.DeserializeRuleSet(active.RulesJson)),
                RuleText.FormatRuleSet(RuleJson.DeserializeRuleSet(target.RulesJson)),
                active is null ? "(no active version)" : $"v{active.Version} (active)", $"v{version}");

            if (!args.Flag("yes"))
            {
                if (!env.Interactive) throw new CliException("Pass --yes to roll back without a prompt.", 2);
                env.Out.Write("Roll back? [y/N] ");
                var answer = env.In.ReadLine()?.Trim();
                if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
                {
                    env.Out.WriteLine("Not rolled back.");
                    return 1;
                }
            }

            var saved = await client.RevertAsync(sys, version, ct);
            env.Out.WriteLine($"{sys}: v{version} is active again as v{saved.Version}");
            return 0;
        }

        private static int ParseVersion(string text) =>
            int.TryParse(text.TrimStart('v', 'V'), out var v) && v > 0 ? v : throw new CliException($"'{text}' isn't a version number.", 2);

        private static string Single(IReadOnlyList<string> subsystems, string command) => subsystems.Count == 1
            ? subsystems[0]
            : throw new CliException($"{command}: pick one with --only database or --only storage.", 2);

        public async Task<int> DiffAsync(CancellationToken ct)
        {
            var locals = LoadAll();
            var client = await ClientAsync(ct);
            foreach (var local in locals)
            {
                var live = RuleJson.DeserializeRuleSet(await client.GetLiveAsync(local.Subsystem, ct));
                if (live.Equals(local.RuleSet))
                {
                    env.Out.WriteLine($"{local.Subsystem}: no differences");
                    continue;
                }
                env.Out.WriteLine($"{local.Subsystem}:");
                WriteDiff(RuleText.FormatRuleSet(live), RuleText.FormatRuleSet(local.RuleSet), "live (console)", local.Display);
            }
            return 0;
        }

        public async Task<int> PullAsync(CancellationToken ct)
        {
            var config = RequireConfig();
            var client = await ClientAsync(ct);
            var toStdout = args.Flag("stdout");
            foreach (var sys in SelectedFiles())
            {
                var text = RuleText.FormatRuleSet(RuleJson.DeserializeRuleSet(await client.GetLiveAsync(sys, ct)));
                if (toStdout)
                {
                    env.Out.WriteLine($"// {sys}");
                    env.Out.WriteLine(text);
                    continue;
                }
                var path = config.RulesPath(sys);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text.EndsWith('\n') ? text : text + "\n");
                env.Out.WriteLine($"{sys}: wrote {Path.GetRelativePath(env.WorkingDirectory, path)} (comments aren't kept on the console)");
            }
            return 0;
        }

        private void WriteDiff(string before, string after, string beforeLabel, string afterLabel)
        {
            env.Out.WriteLine($"  --- {beforeLabel}");
            env.Out.WriteLine($"  +++ {afterLabel}");
            foreach (var line in LineDiff.Compute(before, after))
                env.Out.WriteLine("  " + line);
        }

        private string DefaultNote()
        {
            var sha = Git("rev-parse --short HEAD");
            var dirty = sha is not null && !string.IsNullOrEmpty(Git("status --porcelain"));
            return sha is null ? "winche rules deploy" : $"winche rules deploy @ {sha}{(dirty ? " (uncommitted changes)" : "")}";
        }

        private string? Git(string arguments)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
                {
                    WorkingDirectory = Config?.Directory ?? env.WorkingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p is null) return null;
                var output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(5000);
                return p.ExitCode == 0 ? output : null;
            }
            catch
            {
                return null;
            }
        }
    }

    private static string Count(RuleSet ruleSet)
    {
        int matches = 0, allows = 0;
        void Walk(IReadOnlyList<MatchBlock> blocks)
        {
            foreach (var b in blocks) { matches++; allows += b.Allow.Count; Walk(b.Matches); }
        }
        Walk(ruleSet.Matches);
        return $"{matches} match{(matches == 1 ? "" : "es")}, {allows} allow{(allows == 1 ? "" : "s")}";
    }

    private static IReadOnlyList<string> UnknownFunctions(RuleSet ruleSet) => RuleSetValidation.UnknownFunctions(ruleSet);
}

/// <summary>Positional words plus <c>--name value</c> / <c>--flag</c> options.</summary>
internal sealed class Args
{
    private static readonly HashSet<string> Flags = ["dry-run", "force", "stdout", "help", "version", "skip-tests", "yes", "clear", "quiet", "verbose", "reauth", "no-browser", "no-build",
        "rotate", "path-style", "finish", "cancel", "keep-old", "follow", "remove-secrets", "apple-development"];
    private static readonly HashSet<string> Valued = ["console", "token", "config", "only", "message", "folder", "title", "role", "issuer", "subject", "audience", "port", "project", "add", "remove", "host", "pg", "data", "url", "key", "out", "platforms", "app",
        "id", "code", "email", "first-name", "last-name", "name", "products", "scopes", "days", "db-name", "db-host", "db-port", "db-user", "db-ssl",
        "role", "sdk", "android-package", "apple-bundle-id", "apple-team-id", "web-origin", "token-minutes",
        "provider", "bucket", "region", "service-url", "access-key", "to", "product", "level", "since", "search", "limit", "from-file", "channel",
        "ref", "access-key-id"];
    /// <summary>Options that may be given more than once (--set KEY=value --set …).</summary>
    private static readonly HashSet<string> Repeated = ["set"];
    private static readonly Dictionary<string, string> Short = new() { ["-m"] = "message", ["-h"] = "help", ["-y"] = "yes", ["-P"] = "project", ["-f"] = "follow" };

    public List<string> Positional { get; } = [];
    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);

    public static Args Parse(string[] argv)
    {
        var a = new Args();
        for (var i = 0; i < argv.Length; i++)
        {
            var arg = argv[i];
            string? name = Short.GetValueOrDefault(arg);
            string? inline = null;
            if (name is null && arg.StartsWith("--", StringComparison.Ordinal))
            {
                name = arg[2..];
                var eq = name.IndexOf('=');
                if (eq >= 0) { inline = name[(eq + 1)..]; name = name[..eq]; }
            }
            if (name is null) { a.Positional.Add(arg); continue; }

            if (Flags.Contains(name)) { a._options[name] = null; continue; }
            if (Repeated.Contains(name))
            {
                var item = inline ?? (i + 1 < argv.Length ? argv[++i] : throw new CliException($"{arg} needs a value.", 2));
                if (!a._many.TryGetValue(name, out var items)) a._many[name] = items = [];
                items.Add(item);
                continue;
            }
            if (!Valued.Contains(name)) throw new CliException($"Unknown option '{arg}'. Run: winche --help", 2);
            var value = inline ?? (i + 1 < argv.Length ? argv[++i] : throw new CliException($"{arg} needs a value.", 2));
            a._options[name] = value;
        }
        return a;
    }

    public bool Flag(string name) => _options.ContainsKey(name);
    public string? Value(string name) => _options.GetValueOrDefault(name);
    public IReadOnlyList<string> Values(string name) => _many.TryGetValue(name, out var v) ? v : [];
    private readonly Dictionary<string, List<string>> _many = new(StringComparer.Ordinal);
}
