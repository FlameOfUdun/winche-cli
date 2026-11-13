using System.Reflection;
using Winche.Rules;
using Winche.Rules.Evaluation;
using Winche.Rules.Expressions;
using Winche.Rules.Json;
using Winche.Rules.Text;

namespace Winche.Cli;

/// <summary>The <c>winche</c> command line.</summary>
internal static class CliApp
{
    private const string Usage = """
        Usage: winche <command> [options]

        Commands:
          init                 Create winche.json and starter database.rules / storage.rules
          use [<alias>]        Pick the project (console) to use here, or list them
                               (--add <alias> <url>, --remove <alias>, --clear)
          login                Sign in to the console in your browser (Keycloak)
          logout               Forget the saved sign-in for the console
          rules validate       Check the rules files (offline)
          rules test           Run the allow/deny cases in the test files (offline)
          rules deploy         Run the tests, then publish the rules files as new versions on the console
          rules history [<v>]  List the console's rule versions, or show version <v> against the active one
          rules rollback <v>   Make version <v> active again (saved as a new version)
          rules diff           Show how the rules files differ from what the console enforces
          rules pull           Write the console's live rules into the rules files
          configure            Write lib/winche_options.dart for a Flutter or Dart app, from a Winche server
                               (--url; default the emulators), like flutterfire configure
          functions init [<dir>]
                               Make a functions folder (default functions/): an example, deno.json, types for VS Code
          functions deploy [<dir>]
                               Deploy the functions folder to a server (--url and --key, or WINCHE_SERVER and WINCHE_DEPLOY_KEY)
          emulators start      Run the database, storage, functions and console locally (Postgres in Docker; rules and functions reloaded on save)
          emulators exec <cmd> Start the emulators, run <cmd> against them (e.g. your tests), stop, exit with its code
          emulators token <uid> [claim=value …]
                               Print a sign-in token the emulators accept
          emulators idtoken <google|apple> <email>
                               Print a fake Google or Apple ID token for the emulators' /auth/v1/signIn/idp
          lsp                  Run the language server for editors (the Winche VS Code extension starts it)

        Options:
          --console <url>      Console URL, e.g. https://api.example.com/_console (or WINCHE_CONSOLE, or winche.json)
          -P, --project <name> A project from winche.json's "projects" (instead of the one picked with winche use)
          --token <jwt>        Bearer token to use instead of a saved sign-in (or WINCHE_TOKEN; for CI)
          --config <path>      winche.json to use (default: the nearest one upward)
          --only <list>        database, storage, or database,storage (default: every file in winche.json)
          -m, --message <text> Note for the deployed versions (deploy)
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
          --url <server>       functions deploy: the Winche server, e.g. https://api.example.com
          --key <deploy key>   functions deploy: the key from the console (Functions → VS Code)
          --out <file>         configure: where to write (default lib/winche_options.dart)
          --platforms <list>   configure: android,ios,macos,web,windows,linux (default: the app's platform folders)
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
            var command = string.Join(' ', a.Positional.Take(a.Positional[0] is "rules" or "emulators" or "functions" ? 2 : 1));
            var run = new Run(env, a, http);
            return command switch
            {
                "init" => run.Init(),
                "use" => run.Use(a.Positional.ElementAtOrDefault(1), a.Positional.ElementAtOrDefault(2)),
                "login" => await run.LoginAsync(ct),
                "logout" => run.Logout(),
                "rules validate" => run.Validate(),
                "rules test" => await run.TestAsync(ct),
                "rules history" => await run.HistoryAsync(a.Positional.ElementAtOrDefault(2), ct),
                "rules rollback" => await run.RollbackAsync(a.Positional.ElementAtOrDefault(2), ct),
                "rules deploy" => await run.DeployAsync(ct),
                "rules diff" => await run.DiffAsync(ct),
                "rules pull" => await run.PullAsync(ct),
                "configure" => await run.ConfigureAsync(ct),
                "functions init" => run.FunctionsInit(a.Positional.ElementAtOrDefault(2)),
                "functions deploy" => await run.FunctionsDeployAsync(a.Positional.ElementAtOrDefault(2), ct),
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

    private sealed class Run(CliEnvironment env, Args args, HttpClient http)
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
            ?? throw new CliException($"Which console? Pass --console <url>, set WINCHE_CONSOLE, or add \"projects\" (winche use --add <alias> <url>) or \"console\" to {ProjectConfig.FileName}.");

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

            var config = new ProjectConfig { Console = args.Value("console") };
            foreach (var sys in Selected(ProjectConfig.Subsystems))
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
            env.Out.WriteLine(config.Console is null
                ? $"Next: add \"console\" to {ProjectConfig.FileName}, run winche login, then winche rules deploy."
                : "Next: winche login, then winche rules deploy.");
            return 0;
        }

        private static string Starter(string sys) => sys == "database"
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

        private static string StarterTests(string sys) => sys == "database"
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

        // ----- configure ---------------------------------------------------------------------------

        /// <summary>
        /// Reads the server's /.well-known/winche and writes lib/winche_options.dart: a DefaultWincheOptions per
        /// platform, for WincheFlutter.initializeApp(options: DefaultWincheOptions.currentPlatform).
        /// </summary>
        public async Task<int> ConfigureAsync(CancellationToken ct)
        {
            var url = (args.Value("url") ?? env.GetVariable("WINCHE_SERVER") ?? $"http://localhost:{args.Value("port") ?? "9090"}").TrimEnd('/');
            if (!Uri.TryCreate(url, UriKind.Absolute, out var server) || server.Scheme is not ("http" or "https"))
                throw new CliException($"{url} isn't an address. Pass --url https://api.example.com", 2);

            Winche.Console.Apps.AppSetup setup;
            try
            {
                using var response = await http.GetAsync(new Uri(server, "/.well-known/winche"), ct);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    throw new CliException($"{url} doesn't publish its setup (/.well-known/winche). Is it a Winche Server or the emulators, up to date?");
                response.EnsureSuccessStatusCode();
                setup = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<Winche.Console.Apps.AppSetup>(
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
                : Winche.Console.Apps.AppSetup.AllPlatforms.Where(p => Directory.Exists(Path.Combine(env.WorkingDirectory, p))).ToArray();
            var unknown = platforms.Where(p => !Winche.Console.Apps.AppSetup.AllPlatforms.Contains(p.ToLowerInvariant())).ToList();
            if (unknown.Count > 0)
                throw new CliException($"Unknown platform {string.Join(", ", unknown)}: use {string.Join(", ", Winche.Console.Apps.AppSetup.AllPlatforms)}.", 2);
            if (platforms.Length == 0) platforms = [.. Winche.Console.Apps.AppSetup.AllPlatforms];

            var target = Path.GetFullPath(args.Value("out") ?? Path.Combine("lib", "winche_options.dart"), env.WorkingDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, setup.DartOptionsFile(platforms), ct);

            env.Out.WriteLine($"Wrote {Path.GetRelativePath(env.WorkingDirectory, target)} for project {setup.ProjectId} at {setup.BaseUrl}");
            env.Out.WriteLine($"  platforms: {string.Join(", ", platforms)}");
            env.Out.WriteLine($"  services:  {(setup.Services.Count == 0 ? "none" : string.Join(", ", setup.Services.Select(s => s.Id)))}");
            if (setup.Providers.Where(p => p.ClientIds.Count > 1).Select(p => p.Name).ToList() is { Count: > 0 } several)
                env.Out.WriteLine($"  {string.Join(", ", several)}: several client ids; the file lists them for you to pick one per platform.");
            if (!File.Exists(Path.Combine(env.WorkingDirectory, "pubspec.yaml")))
                env.Out.WriteLine("  (no pubspec.yaml here: run this in your app's folder, or pass --out)");
            env.Out.WriteLine("Next: flutter pub add winche_flutter winche_core, then in main():");
            env.Out.WriteLine("  final app = await WincheFlutter.initializeApp(options: DefaultWincheOptions.currentPlatform);");
            return 0;
        }

        public async Task<int> FunctionsDeployAsync(string? dir, CancellationToken ct)
        {
            var url = args.Value("url") ?? env.GetVariable("WINCHE_SERVER")
                ?? throw new CliException("Which server? Pass --url https://api.example.com (or set WINCHE_SERVER).", 2);
            var key = args.Value("key") ?? env.GetVariable("WINCHE_DEPLOY_KEY")
                ?? throw new CliException("Pass --key with the deploy key from the console (Functions → VS Code), or set WINCHE_DEPLOY_KEY.", 2);
            if (!Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var server)) throw new CliException($"{url} isn't an address.", 2);
            return await FunctionsFolder.DeployAsync(http, server, key, FunctionsDir(dir), args.Value("message"), env, ct);
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
                Functions = FunctionsDir(null) is var fdir && Directory.Exists(fdir) ? fdir : null,
                Quiet = args.Flag("quiet") || command is not null,
                Verbose = args.Flag("verbose"),
                ProjectId = config?.ProjectId ?? "demo",
            };

            if (options.Functions is not null) options = options with { DenoPath = await DenoInstaller.EnsureAsync(env, http, ct) };
            await using var host = await Emulator.EmulatorHost.StartAsync(options, env.Out, ct);
            if (command is null)
            {
                host.PrintBanner(options.DatabaseRules, options.StorageRules);
                env.Out.WriteLine("Press Ctrl+C to stop.");
                using var either = CancellationTokenSource.CreateLinkedTokenSource(ct, host.Stopping);
                try { await Task.Delay(Timeout.Infinite, either.Token); }
                catch (OperationCanceledException) { env.Out.WriteLine("Stopping…"); }
                return 0;
            }

            env.Out.WriteLine($"Emulators at {host.BaseUrl}; running: {command}");
            var psi = OperatingSystem.IsWindows()
                ? new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", command])
                : new System.Diagnostics.ProcessStartInfo("/bin/sh", ["-c", command]);
            psi.WorkingDirectory = env.WorkingDirectory;
            psi.Environment["WINCHE_EMULATOR"] = host.BaseUrl;
            psi.Environment["WINCHE_EMULATOR_HOST"] = new Uri(host.BaseUrl).Authority;
            psi.Environment["WINCHE_DATABASE_URL"] = host.BaseUrl + "/documents/v1";
            psi.Environment["WINCHE_STORAGE_URL"] = host.BaseUrl + "/files/v1";
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

        public async Task<int> LoginAsync(CancellationToken ct)
        {
            var console = ConsoleUrl();
            var port = KeycloakLogin.DefaultPort;
            if (args.Value("port") is { } p && (!int.TryParse(p, out port) || port is < 1 or > 65535))
                throw new CliException($"--port {p} isn't a port number.", 2);

            var login = await new KeycloakLogin(env, http).LoginAsync(console, port, ct);
            new CredentialStore(env.ConfigDirectory).Put(console, login);
            env.Out.WriteLine($"Signed in to {CredentialStore.Key(console)}{(login.User is null ? "" : $" as {login.User}")}.");
            return 0;
        }

        public int Logout()
        {
            var console = ConsoleUrl();
            var store = new CredentialStore(env.ConfigDirectory);
            if (store.Get(console) is null)
            {
                env.Out.WriteLine($"Not signed in to {CredentialStore.Key(console)}.");
                return 0;
            }
            store.Put(console, null);
            env.Out.WriteLine($"Signed out of {CredentialStore.Key(console)}.");
            return 0;
        }

        private async Task<ConsoleClient> ClientAsync(CancellationToken ct)
        {
            var target = Target();
            var console = target.Url;
            if (target.Alias is not null) env.Out.WriteLine($"Project: {target.Describe()}");
            var token = args.Value("token") ?? env.GetVariable("WINCHE_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
            {
                var store = new CredentialStore(env.ConfigDirectory);
                var saved = store.Get(console)
                            ?? throw new CliException($"Not signed in to {CredentialStore.Key(console)}. Run: winche login");
                var fresh = await new KeycloakLogin(env, http).RefreshIfNeededAsync(saved, ct)
                            ?? throw new CliException($"Your sign-in to {CredentialStore.Key(console)} has ended. Run: winche login");
                if (!ReferenceEquals(fresh, saved)) store.Put(console, fresh);
                token = fresh.AccessToken;
            }
            return new ConsoleClient(http, console, token);
        }

        // ----- rules -----------------------------------------------------------------------------

        private IReadOnlyList<string> Selected(IEnumerable<string> available)
        {
            var list = available.ToList();
            if (args.Value("only") is not { } only) return list;
            var wanted = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var w in wanted)
                if (!ProjectConfig.Subsystems.Contains(w))
                    throw new CliException($"--only {w}: expected database or storage.", 2);
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
    private static readonly HashSet<string> Flags = ["dry-run", "force", "stdout", "help", "version", "skip-tests", "yes", "clear", "quiet", "verbose"];
    private static readonly HashSet<string> Valued = ["console", "token", "config", "only", "message", "port", "project", "add", "remove", "host", "pg", "data", "url", "key", "out", "platforms"];
    private static readonly Dictionary<string, string> Short = new() { ["-m"] = "message", ["-h"] = "help", ["-y"] = "yes", ["-P"] = "project" };

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
            if (!Valued.Contains(name)) throw new CliException($"Unknown option '{arg}'. Run: winche --help", 2);
            var value = inline ?? (i + 1 < argv.Length ? argv[++i] : throw new CliException($"{arg} needs a value.", 2));
            a._options[name] = value;
        }
        return a;
    }

    public bool Flag(string name) => _options.ContainsKey(name);
    public string? Value(string name) => _options.GetValueOrDefault(name);
}
