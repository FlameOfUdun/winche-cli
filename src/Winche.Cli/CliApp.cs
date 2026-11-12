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
          login                Sign in to the console in your browser (Keycloak)
          logout               Forget the saved sign-in for the console
          rules validate       Check the rules files (offline)
          rules deploy         Publish the rules files as new versions on the console
          rules diff           Show how the rules files differ from what the console enforces
          rules pull           Write the console's live rules into the rules files

        Options:
          --console <url>      Console URL, e.g. https://api.example.com/_console (or WINCHE_CONSOLE, or winche.json)
          --token <jwt>        Bearer token to use instead of a saved sign-in (or WINCHE_TOKEN; for CI)
          --config <path>      winche.json to use (default: the nearest one upward)
          --only <list>        database, storage, or database,storage (default: every file in winche.json)
          -m, --message <text> Note for the deployed versions (deploy)
          --dry-run            Show what deploy would change, without saving
          --force              Deploy even when the console already has these rules
          --stdout             Print pulled rules instead of writing the files (pull)
          --port <n>           Loopback port for the sign-in callback (login; default 9005)
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
            var command = string.Join(' ', a.Positional.Take(a.Positional[0] == "rules" ? 2 : 1));
            var run = new Run(env, a, http);
            return command switch
            {
                "init" => run.Init(),
                "login" => await run.LoginAsync(ct),
                "logout" => run.Logout(),
                "rules validate" => run.Validate(),
                "rules deploy" => await run.DeployAsync(ct),
                "rules diff" => await run.DiffAsync(ct),
                "rules pull" => await run.PullAsync(ct),
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

        private Uri ConsoleUrl()
        {
            var raw = args.Value("console") ?? env.GetVariable("WINCHE_CONSOLE") ?? Config?.Console
                      ?? throw new CliException($"Which console? Pass --console <url>, set WINCHE_CONSOLE, or add \"console\" to {ProjectConfig.FileName}.");
            if (!Uri.TryCreate(raw.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                throw new CliException($"'{raw}' isn't a console URL (expected https://host/prefix).");
            return uri;
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
                config.Rules[sys] = file;
                var full = Path.Combine(env.WorkingDirectory, file);
                if (File.Exists(full)) { env.Out.WriteLine($"  kept    {file}"); continue; }
                File.WriteAllText(full, Starter(sys));
                env.Out.WriteLine($"  created {file}");
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
            var console = ConsoleUrl();
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

        public async Task<int> DeployAsync(CancellationToken ct)
        {
            var locals = LoadAll();          // nothing is deployed unless every file is valid
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

    private static SortedSet<string> UnknownFunctions(RuleSet ruleSet)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        void Expr(RuleExpression x)
        {
            switch (x)
            {
                case CallExpression c:
                    if (!RuleFunctions.Names.Contains(c.Name)) names.Add(c.Name);
                    foreach (var a in c.Args) Expr(a);
                    break;
                case MemberExpression m: Expr(m.Target); break;
                case IndexExpression ix: Expr(ix.Target); Expr(ix.Index); break;
                case CompareExpression cmp: Expr(cmp.Left); Expr(cmp.Right); break;
                case AndExpression a: foreach (var o in a.Operands) Expr(o); break;
                case OrExpression o: foreach (var p in o.Operands) Expr(p); break;
                case NotExpression n: Expr(n.Operand); break;
                case InExpression i: Expr(i.Item); Expr(i.Collection); break;
                case ConditionalExpression cond: Expr(cond.Condition); Expr(cond.Then); Expr(cond.Else); break;
            }
        }
        void Block(MatchBlock b)
        {
            foreach (var a in b.Allow) Expr(a.Condition);
            foreach (var m in b.Matches) Block(m);
        }
        foreach (var m in ruleSet.Matches) Block(m);
        return names;
    }
}

/// <summary>Positional words plus <c>--name value</c> / <c>--flag</c> options.</summary>
internal sealed class Args
{
    private static readonly HashSet<string> Flags = ["dry-run", "force", "stdout", "help", "version"];
    private static readonly HashSet<string> Valued = ["console", "token", "config", "only", "message", "port"];
    private static readonly Dictionary<string, string> Short = new() { ["-m"] = "message", ["-h"] = "help" };

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
