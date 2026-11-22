using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Winche.Cli;

/// <summary>
/// What the console does, from the terminal: setting a server up, two-step sign-in, projects, products, service accounts and
/// settings files. Everything goes through the console's API, as the signed-in account (or a service account where allowed).
/// </summary>
internal static partial class CliApp
{
    internal sealed partial class Run
    {
        // ----- prompts -----------------------------------------------------------------------------

        /// <summary><paramref name="given"/>, or asked for (or <paramref name="fallback"/> when nobody can answer, or the answer's empty).</summary>
        private string Ask(string label, string? given, string? fallback = null, string? option = null)
        {
            if (!string.IsNullOrWhiteSpace(given)) return given.Trim();
            if (!env.Interactive)
                return fallback ?? throw new CliException($"{label}: pass {option ?? "it as an option (see winche --help)"}; there's no terminal to ask in.", 2);
            env.Out.Write(fallback is { Length: > 0 } ? $"{label} [{fallback}]: " : $"{label}: ");
            var answer = env.In.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(answer)) return fallback ?? throw new CliException($"{label} is needed.", 2);
            return answer;
        }

        /// <summary>A secret from <paramref name="variable"/>, or typed without being shown (twice, when <paramref name="confirm"/>).</summary>
        private string AskSecret(string label, string variable, bool confirm)
        {
            if (env.GetVariable(variable) is { Length: > 0 } fromEnv) return fromEnv;
            if (!env.Interactive) throw new CliException($"{label}: set {variable}; there's no terminal to ask in.", 2);
            env.Out.Write($"{label}: ");
            var first = env.ReadSecret() ?? "";
            if (confirm)
            {
                env.Out.Write($"{label} again: ");
                if (env.ReadSecret() != first) throw new CliException("The two didn't match.", 2);
            }
            if (first.Length == 0) throw new CliException($"{label} is needed.", 2);
            return first;
        }

        /// <summary>
        /// Where a project's database goes (<c>--db-name</c>, <c>--db-host</c>, …; the password in <c>WINCHE_DB_PASSWORD</c>),
        /// or null: a new database on the server's own PostgreSQL.
        /// </summary>
        private object? DatabaseTarget()
        {
            var name = args.Value("db-name");
            var host = args.Value("db-host");
            if (name is null && host is null) return null;
            int? port = null;
            if (args.Value("db-port") is { } p)
                port = int.TryParse(p, out var n) && n is > 0 and < 65536 ? n : throw new CliException($"--db-port {p} isn't a port number.", 2);
            string? password = null;
            if (host is not null) password = env.GetVariable("WINCHE_DB_PASSWORD") ?? (env.Interactive ? AskSecret("Database password", "WINCHE_DB_PASSWORD", confirm: false) : null);
            return new { database = name, host, port, username = args.Value("db-user"), password, sslMode = args.Value("db-ssl") };
        }

        /// <summary>
        /// The server's console from <c>--url</c> (a server, <c>https://api.example.com</c>, or its console), else the
        /// one this project uses. Accounts are the server's: account commands work at its console.
        /// </summary>
        private Uri ServerConsole()
        {
            if (args.Value("url") is { } url)
            {
                if (!Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
                    throw new CliException($"--url {url} isn't an http(s) address.", 2);
                return ConsoleAddress.ServerOf(ConsoleTarget.Parse(url));
            }
            if (Config is not null || args.Value("console") is not null || args.Value("project") is not null || env.GetVariable("WINCHE_CONSOLE") is not null)
                return CredentialStore.ServerOf(ConsoleUrl());
            // No project here yet: the one server you're signed in to (right after winche setup, say).
            var servers = new CredentialStore(env.ConfigDirectory).All().Keys.ToList();
            return servers.Count == 1 ? new Uri(servers[0].TrimEnd('/') + "/") : CredentialStore.ServerOf(ConsoleUrl());
        }

        // ----- setup -------------------------------------------------------------------------------

        /// <summary>
        /// <c>winche setup --url &lt;server&gt;</c>: a new server's first-run setup, like the console's: the setup code (from the
        /// server's log) and the admin account (signed in here). Projects come next: <c>winche projects:create</c>.
        /// </summary>
        public async Task<int> SetupAsync(CancellationToken ct)
        {
            if (args.Value("url") is null && args.Value("console") is null && env.GetVariable("WINCHE_CONSOLE") is null)
                throw new CliException("Usage: winche setup --url https://api.example.com", 2);
            var console = ServerConsole();
            var store = new CredentialStore(env.ConfigDirectory);

            if (store.Active(console) is null)
            {
                var code = args.Value("code") ?? env.GetVariable("WINCHE_SETUP_CODE") ?? Ask("Setup code (the server's log: \"Setup code: …\")", null, option: "--code (or set WINCHE_SETUP_CODE)");
                var email = Ask("Your email", args.Value("email"), option: "--email");
                var first = Ask("First name", args.Value("first-name"), "");
                var last = Ask("Last name", args.Value("last-name"), "");
                var password = AskSecret("Password", "WINCHE_ADMIN_PASSWORD", confirm: true);
                var (status, body) = await Anonymous(console).SendAsync(HttpMethod.Post, "session/setup",
                    new { setupCode = code, email, firstName = first, lastName = last, password, cli = ConsoleLogin.SessionName }, ct);
                if (status == HttpStatusCode.Conflict)
                    throw new CliException($"{CredentialStore.Key(console)} is already set up. Sign in: winche login --url {CredentialStore.Key(console)}");
                if (status != HttpStatusCode.OK || body["session"] is not { } session)
                    throw new CliException($"Setup failed: {(body["error"] is JsonValue v ? v.ToString() : ConsoleClient.ErrorText(body, status))}");
                store.Put(console, ConsoleLogin.FromSession(session));
                env.Out.WriteLine($"Made the admin account {email}, and signed in here.");
            }

            env.Out.WriteLine();
            env.Out.WriteLine("Next:");
            env.Out.WriteLine("  winche 2fa enable                    # changing settings needs two-step sign-in (unless the server turns that off)");
            env.Out.WriteLine("  winche projects:create <id> --name …  # a project: its own database, users and files, at /projects/<id>");
            env.Out.WriteLine($"  winche init --console {ConsoleAddress.Describe(console)} --project <id>   # this folder's project");
            return 0;
        }

        private ConsoleClient Anonymous(Uri console) => new(http, console, "");

        private static string[] Products(string? list)
        {
            var products = (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p == "auth" ? "authentication" : p).ToArray();
            if (products.FirstOrDefault(p => !ProductNames.Contains(p)) is { } unknown)
                throw new CliException($"'{unknown}' isn't a product: {string.Join(", ", ProductNames)}.", 2);
            return products;
        }

        private static readonly string[] ProductNames = ["database", "storage", "authentication", "functions", "assets"];

        // ----- two-step sign-in --------------------------------------------------------------------

        /// <summary><c>winche 2fa enable</c>: turns on two-step sign-in for your account with an authenticator app.</summary>
        public async Task<int> TwoFactorEnableAsync(CancellationToken ct)
        {
            var client = await ClientAsync(ServerConsole(), ct);
            var (status, started) = await client.SendAsync(HttpMethod.Post, "session/2fa/setup", new { }, ct);
            if (status != HttpStatusCode.OK) throw new CliException($"Can't start two-step sign-in: {ConsoleClient.ErrorText(started, status)}");
            env.Out.WriteLine("Add this to your authenticator app (most can open the link, or take the key by hand):");
            env.Out.WriteLine($"  {started["authenticatorUri"]}");
            env.Out.WriteLine($"  Key: {started["sharedKey"]}");
            for (var attempt = 1; ; attempt++)
            {
                var code = Ask("The 6-digit code it shows", args.Value("code"), option: "--code");
                var (ok, body) = await client.SendAsync(HttpMethod.Post, "session/2fa/enable", new { code }, ct);
                if (ok == HttpStatusCode.OK)
                {
                    env.Out.WriteLine("Two-step sign-in is on. Keep these recovery codes somewhere safe (each works once, if you lose the app):");
                    foreach (var c in body["recoveryCodes"]?.AsArray() ?? []) env.Out.WriteLine($"  {c}");
                    return 0;
                }
                if (attempt >= 3 || args.Value("code") is not null) throw new CliException($"Not turned on: {ConsoleClient.ErrorText(body, ok)}");
                env.Out.WriteLine("That code didn't work; try the next one.");
            }
        }

        // ----- projects and products ---------------------------------------------------------------

        /// <summary><c>winche projects:create &lt;id&gt; [--name …] [--db-…]</c> (server admins).</summary>
        public async Task<int> ProjectsCreateAsync(string? id, CancellationToken ct)
        {
            var console = ServerConsole();
            id = Ask("Project id (lower-case letters, digits and dashes)", id, option: "the id: winche projects:create <id>");
            var name = Ask("Project name", args.Value("name"), id);
            var client = await ClientAsync(console, ct);
            var (status, body) = await client.SendAsync(HttpMethod.Post, "projects", new { name, id, database = DatabaseTarget() }, ct);
            if (status != HttpStatusCode.OK) throw new CliException($"Making the project failed: {ConsoleClient.ErrorText(body, status)}");
            var url = ConsoleAddress.Describe(ConsoleTarget.ForProject(console, id));
            env.Out.WriteLine($"Made the project {name} ({id}): {url}");
            env.Out.WriteLine($"Use it: winche use --add {id} {url}  (or --project {id})");
            return 0;
        }

        /// <summary><c>winche products</c>: the project's products, started or not.</summary>
        public async Task<int> ProductsAsync(CancellationToken ct)
        {
            var project = await (await ClientAsync(ct)).GetJsonAsync("project", ct);
            foreach (var p in project["products"]?.AsArray() ?? [])
            {
                var state = p!["ready"]?.GetValue<bool>() == true ? "started"
                    : p["setup"]?["state"]?.GetValue<string>() is "running" ? "setting up"
                    : p["setup"]?["state"]?.GetValue<string>() is "failed" ? "setup failed"
                    : p["started"]?.GetValue<bool>() == true ? "starting" : "off";
                env.Out.WriteLine($"  {p["id"],-16} {state}");
            }
            env.Out.WriteLine("Start one: winche products:start <product>");
            return 0;
        }

        /// <summary><c>winche products:start database storage …</c>: starts them and follows each one's setup.</summary>
        public async Task<int> ProductsStartAsync(IReadOnlyList<string> names, CancellationToken ct)
        {
            if (names.Count == 0) throw new CliException($"Usage: winche products:start <product> … ({string.Join(", ", ProductNames)})", 2);
            var products = Products(string.Join(',', names));
            var client = await ClientAsync(ct);
            var failed = false;
            foreach (var product in products)
            {
                var (status, body) = await client.SendAsync(HttpMethod.Post, $"project/products/{product}/start", new { }, ct);
                if (status is not (HttpStatusCode.OK or HttpStatusCode.Accepted)) throw new CliException($"{product}: {ConsoleClient.ErrorText(body, status)}");
                var shown = 0;
                while (true)
                {
                    var steps = body["steps"]?.AsArray() ?? [];
                    foreach (var step in steps.Skip(shown).Where(s => s?["state"]?.GetValue<string>() is "done" or "failed"))
                    {
                        var ok = step!["state"]!.GetValue<string>() == "done";
                        env.Out.WriteLine($"  {(ok ? "✓" : "✗")} {product}: {step["title"]}{(ok ? "" : $" — {step["error"]}")}");
                        shown++;
                    }
                    var state = body["state"]?.GetValue<string>();
                    if (state == "done") { env.Out.WriteLine($"{product} is started."); break; }
                    if (state == "failed") { env.Error.WriteLine($"{product}'s setup failed (run winche products:start {product} to try again)."); failed = true; break; }
                    await Task.Delay(TimeSpan.FromMilliseconds(500), env.Time, ct);
                    (status, body) = await client.SendAsync(HttpMethod.Get, $"project/products/{product}/setup", null, ct);
                    if (status != HttpStatusCode.OK) throw new CliException($"{product}: {ConsoleClient.ErrorText(body, status)}");
                }
            }
            return failed ? 1 : 0;
        }

        // ----- apps --------------------------------------------------------------------------------

        /// <summary><c>winche apps</c>: the project's apps (Integrity), each with today's verified requests.</summary>
        public async Task<int> AppsListAsync(CancellationToken ct)
        {
            var (status, body) = await (await ClientAsync(ct)).SendAsync(HttpMethod.Get, "integrity", null, ct);
            if (status != HttpStatusCode.OK) throw new CliException(ConsoleClient.ErrorText(body, status));
            var apps = body["apps"]?.AsArray() ?? [];
            if (apps.Count == 0) env.Out.WriteLine("No apps yet. Add one: winche apps:create <name> --android-package … --apple-bundle-id … --apple-team-id …");
            foreach (var a in apps)
            {
                var apple = a!["appleBundleId"]?.ToString() is { Length: > 0 } bundle ? (a["appleTeamId"]?.ToString() is { Length: > 0 } team ? $"{team}.{bundle}" : bundle) : null;
                var where = new[] { a["androidPackage"]?.ToString(), apple }.Where(x => !string.IsNullOrEmpty(x));
                var today = a["usage"]?.AsObject().Sum(u => u.Value?["today"]?.GetValue<long>() ?? 0) ?? 0;
                var devices = a["devices"]?["count"]?.GetValue<long>() is { } n ? $", {n} App Attest device{(n == 1 ? "" : "s")}" : "";
                var dev = a["appleDevelopment"]?.GetValue<bool>() == true ? " (development builds accepted)" : "";
                if (a["webSiteKey"]?.ToString() is { Length: > 0 }) dev += a["turnstileSecret"] is JsonObject ? ", browser check" : ", browser check without its secret key";
                env.Out.WriteLine($"  {a["id"],-24} {a["name"],-20} {a["sdk"],-8} {string.Join(", ", where),-44} {today} verified today{devices}{dev}");
            }
            return 0;
        }

        /// <summary><c>winche apps:create &lt;name&gt;</c>: adds an app, then <c>winche configure --app &lt;id&gt;</c> writes its options.</summary>
        public async Task<int> AppsCreateAsync(string? name, CancellationToken ct)
        {
            name = Ask("App name", name, option: "the name: winche apps:create <name>");
            if (args.Value("web-origin") is not null) throw new CliException("--web-origin is gone: any web page may call the project; Integrity decides which apps get in.", 2);
            var request = new
            {
                name, id = args.Value("id"), sdk = args.Value("sdk") ?? "flutter",
                androidPackage = args.Value("android-package"), appleBundleId = args.Value("apple-bundle-id"), appleTeamId = args.Value("apple-team-id"),
                appleDevelopment = args.Flag("apple-development"),
                androidCloudProjectNumber = args.Value("android-cloud-project") is { } cp ? long.TryParse(cp, out var n) ? n : throw new CliException($"--android-cloud-project {cp}: the Google Cloud project's number.", 2) : (long?)null,
                androidSideloaded = args.Flag("android-sideloaded"),
                androidDeviceIntegrity = args.Value("android-device"),
                androidCertificates = (args.Value("android-certificate") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                webSiteKey = args.Value("web-site-key"),
                webHostnames = (args.Value("web-hostname") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            };
            var client = await ClientAsync(ct);
            var (status, body) = await client.SendAsync(HttpMethod.Post, "integrity/apps", request, ct);
            if (status != HttpStatusCode.OK) throw new CliException($"Not added: {ConsoleClient.ErrorText(body, status)}");
            var id = body["id"]!.GetValue<string>();
            var server = ConsoleUrl().GetLeftPart(UriPartial.Authority);
            var project = ConsoleAddress.ProjectOf(Target().Url) is { } p ? $" --project {p}" : "";
            env.Out.WriteLine($"Added the app {name} ({id}).");
            env.Out.WriteLine($"In the app's folder: winche configure --url {server}{project} --app {id}");
            return 0;
        }

        // ----- integrity ---------------------------------------------------------------------------

        private static readonly string[] IntegrityModes = ["off", "monitor", "enforce"];

        /// <summary><c>winche integrity</c>: each product's mode with today's requests, how long tokens last, and the debug tokens.</summary>
        public async Task<int> IntegrityAsync(CancellationToken ct)
        {
            var (status, body) = await (await ClientAsync(ct)).SendAsync(HttpMethod.Get, "integrity", null, ct);
            if (status != HttpStatusCode.OK) throw new CliException(ConsoleClient.ErrorText(body, status));
            if (body["available"]?.GetValue<bool>() != true) { env.Out.WriteLine("Integrity isn't set up on this console (it comes with project setup)."); return 0; }
            if (body["canSign"]?.GetValue<bool>() != true) env.Out.WriteLine("This server has no master key: it can't make Integrity tokens.");
            var apps = body["apps"]?.AsArray() ?? [];
            long Verified(string product) => apps.Sum(a => a?["usage"]?[product]?["today"]?.GetValue<long>() ?? 0);
            foreach (var product in body["products"]?.AsArray().Select(p => p!.ToString()) ?? [])
            {
                var mode = body["enforcement"]?[product]?.ToString() ?? "monitor";
                var counts = mode == "off" ? "not counted" : $"today {Verified(product)} verified, {body["unverified"]?[product]?["today"]?.GetValue<long>() ?? 0} without a token";
                env.Out.WriteLine($"  {product,-16} {mode,-8} {counts}");
            }
            env.Out.WriteLine($"Tokens last {body["tokenMinutes"]} minutes.");
            if (apps.Any(a => a?["androidPackage"] is not null))
                env.Out.WriteLine(body["playServiceAccount"]?["email"]?.ToString() is { } email ? $"Play Integrity signs in as {email}."
                    : body["playServiceAccount"] is JsonObject ? "Play Integrity has a service account." : "Play Integrity has no service account: winche integrity:play-account <key.json>.");
            var tokens = body["debugTokens"]?.AsArray() ?? [];
            env.Out.WriteLine(tokens.Count == 0 ? "No debug tokens." : "Debug tokens:");
            foreach (var t in tokens) env.Out.WriteLine($"  {t!["id"],-18} {t["name"],-24} for {t["app"]}");
            return 0;
        }

        /// <summary>
        /// <c>winche integrity:set &lt;product&gt; off|monitor|enforce</c> (or <c>--token-minutes &lt;n&gt;</c> alone), with
        /// <c>--reason</c> for the history.
        /// </summary>
        public async Task<int> IntegritySetAsync(string? product, string? mode, CancellationToken ct)
        {
            var minutes = args.Value("token-minutes") is { } m ? int.TryParse(m, out var n) ? n : throw new CliException($"--token-minutes {m}: a number of minutes (30 to 1440).", 2) : (int?)null;
            if (product is null && minutes is null) throw new CliException("Say what to change: winche integrity:set <product> off|monitor|enforce, or --token-minutes <n>.", 2);
            var enforcement = new Dictionary<string, string>();
            if (product is not null)
            {
                mode = mode?.ToLowerInvariant();
                if (mode is null || !IntegrityModes.Contains(mode)) throw new CliException($"Say the mode: winche integrity:set {product} off|monitor|enforce.", 2);
                enforcement[product] = mode;
            }
            var reason = args.Value("message") ?? Ask("Why (for the history)", null, option: "-m \"…\"");
            var (status, body) = await (await ClientAsync(ct)).SendAsync(HttpMethod.Put, "integrity/enforcement", new { enforcement, tokenMinutes = minutes, reason }, ct);
            if (status != HttpStatusCode.OK) throw new CliException($"Not changed: {ConsoleClient.ErrorText(body, status)}");
            if (product is not null) env.Out.WriteLine($"{product}: {body["enforcement"]?[product]}.");
            if (minutes is not null) env.Out.WriteLine($"Tokens last {body["tokenMinutes"]} minutes.");
            return 0;
        }

        /// <summary>
        /// <c>winche integrity:play-account &lt;key.json&gt;</c> (or <c>--ref aws-sm:…</c>): the Google service account Play
        /// Integrity signs in as. Kept as a secret; never shown again.
        /// </summary>
        public async Task<int> PlayAccountAsync(string? file, CancellationToken ct)
        {
            var reference = args.Value("ref");
            if ((file is null) == (reference is null)) throw new CliException("Give the service account's key file (winche integrity:play-account key.json) or --ref aws-sm:….", 2);
            string? value = null;
            if (file is not null)
            {
                var path = Path.GetFullPath(file, env.WorkingDirectory);
                if (!File.Exists(path)) throw new CliException($"No file {file}.", 2);
                value = await File.ReadAllTextAsync(path, ct);
            }
            var (status, body) = await (await ClientAsync(ct)).SendAsync(HttpMethod.Put, "integrity/play-service-account", new { value, reference }, ct);
            if (status != HttpStatusCode.OK) throw new CliException($"Not set: {ConsoleClient.ErrorText(body, status)}");
            env.Out.WriteLine($"Play Integrity signs in as {body["email"]?.ToString() ?? "the service account"}{(reference is null ? "" : $" (read from {reference})")}.");
            if (file is not null) env.Out.WriteLine($"The project keeps it now: you can delete {file}.");
            return 0;
        }

        /// <summary>
        /// <c>winche integrity:turnstile-secret &lt;app&gt;</c>: the app's Turnstile secret key (the browser check), typed
        /// unseen (or <c>WINCHE_TURNSTILE_SECRET</c>), or <c>--ref aws-sm:…</c>.
        /// </summary>
        public async Task<int> TurnstileSecretAsync(string? app, CancellationToken ct)
        {
            app = Ask("App id", app, option: "the app: winche integrity:turnstile-secret <app> (see winche apps)");
            var reference = args.Value("ref");
            var value = reference is null ? AskSecret("Turnstile secret key", "WINCHE_TURNSTILE_SECRET", confirm: false) : null;
            var (status, body) = await (await ClientAsync(ct)).SendAsync(HttpMethod.Put, $"integrity/apps/{Uri.EscapeDataString(app)}/turnstile-secret", new { value, reference }, ct);
            if (status == HttpStatusCode.NotFound) throw new CliException($"No app {app}. See: winche apps");
            if (status != HttpStatusCode.NoContent) throw new CliException($"Not set: {ConsoleClient.ErrorText(body, status)}");
            env.Out.WriteLine($"The browser check for {app} uses that secret key{(reference is null ? "" : $" (read from {reference})")}.");
            return 0;
        }

        /// <summary><c>winche integrity:debug-tokens:create &lt;app&gt; &lt;name&gt;</c>: prints the token, this once.</summary>
        public async Task<int> DebugTokenCreateAsync(string? app, string? name, CancellationToken ct)
        {
            app = Ask("App id", app, option: "the app: winche integrity:debug-tokens:create <app> <name> (see winche apps)");
            name = Ask("Name (where it's used: CI, a simulator)", name, option: "the name: winche integrity:debug-tokens:create <app> <name>");
            var (status, body) = await (await ClientAsync(ct)).SendAsync(HttpMethod.Post, "integrity/debug-tokens", new { app, name }, ct);
            if (status != HttpStatusCode.OK) throw new CliException($"Not made: {ConsoleClient.ErrorText(body, status)}");
            env.Out.WriteLine(body["token"]!.GetValue<string>());
            env.Error.WriteLine($"The debug token {name} for {app} ({body["id"]}): it isn't shown again. Keep it like a password; never ship it in a release build.");
            return 0;
        }

        /// <summary><c>winche integrity:debug-tokens:delete &lt;id&gt;</c>: the Integrity tokens it got stop working.</summary>
        public async Task<int> DebugTokenDeleteAsync(string? id, CancellationToken ct)
        {
            id = Ask("Debug token id", id, option: "the id: winche integrity:debug-tokens:delete <id> (see winche integrity)");
            Confirm($"Remove the debug token {id}? The Integrity tokens it got stop working at once.");
            var (status, body) = await (await ClientAsync(ct)).SendAsync(HttpMethod.Delete, $"integrity/debug-tokens/{Uri.EscapeDataString(id)}", null, ct);
            if (status == HttpStatusCode.NotFound) throw new CliException($"No debug token {id}. See: winche integrity");
            if (status != HttpStatusCode.NoContent) throw new CliException($"Not removed: {ConsoleClient.ErrorText(body, status)}");
            env.Out.WriteLine($"Removed the debug token {id}.");
            return 0;
        }

        // ----- members -----------------------------------------------------------------------------

        private static readonly string[] Roles = ["Admin", "Member", "Viewer"];

        /// <summary><c>winche members</c>: who can use the project (an app's own console without a member list: its accounts' roles).</summary>
        public async Task<int> MembersListAsync(CancellationToken ct)
        {
            var client = await ClientAsync(ct);
            var (status, body) = await client.SendAsync(HttpMethod.Get, "members", null, ct);
            if (status == HttpStatusCode.OK)
            {
                foreach (var a in body["serverAdmins"]?.AsArray() ?? []) env.Out.WriteLine($"  {a!["email"],-32} Admin (server admin)");
                foreach (var m in body["members"]?.AsArray() ?? [])
                    env.Out.WriteLine($"  {m!["email"],-32} {m["role"]}{(m["passwordSet"]?.GetValue<bool>() == false ? " (hasn't set a password yet)" : "")}");
                return 0;
            }
            if (status is not (HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)) throw new CliException(ConsoleClient.ErrorText(body, status));
            // An app's own console (no member list): everyone with an account role (and who's invited).
            foreach (var u in await client.GetListAsync("accounts", ct))
                if (u?["role"]?.ToString() is { Length: > 0 } accountRole)
                    env.Out.WriteLine($"  {u["email"],-32} {accountRole}{(accountRole == "Admin" ? " (server admin)" : "")}{(u["active"]?.GetValue<bool>() == false ? " (turned off)" : "")}");
            foreach (var i in await client.GetListAsync("invites", ct))
                if (i?["status"]?.ToString() == "pending") env.Out.WriteLine($"  {i["email"],-32} {i["role"]} (invited)");
            return 0;
        }

        /// <summary>
        /// <c>winche members:add &lt;email&gt; [--role Member]</c>. In a created project they become its member (an account is
        /// made for someone new, with a link to set its password). An app's own console (no member list) goes by the
        /// accounts' own roles: someone new is invited with that role, an existing account gets it.
        /// </summary>
        public async Task<int> MembersAddAsync(string? email, CancellationToken ct)
        {
            email = Ask("Their email", email, option: "the email: winche members:add <email>");
            var role = args.Value("role") is { } r ? Roles.FirstOrDefault(x => string.Equals(x, r, StringComparison.OrdinalIgnoreCase))
                ?? throw new CliException($"--role {r}: Admin, Member or Viewer.", 2) : "Member";
            var client = await ClientAsync(ct);
            var (status, body) = await client.SendAsync(HttpMethod.Post, "members", new { email, role }, ct);
            if (status == HttpStatusCode.OK)
            {
                env.Out.WriteLine($"Added {email} as {role}.");
                Link(body, body["newAccount"]?.GetValue<bool>() == true ? "It's a new account: they set their password with this link" : null);
                return 0;
            }
            // No member list here (404, or 405 where the console's pages take the path): an app's own console.
            if (status is not (HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)) throw new CliException($"Not added: {ConsoleClient.ErrorText(body, status)}");

            // An app's own console: the account's own role.
            var list = await client.GetListAsync("accounts", ct);
            var existing = list.FirstOrDefault(u => string.Equals(u?["email"]?.ToString(), email, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                (status, body) = await client.SendAsync(HttpMethod.Put, $"accounts/{existing["id"]}", new { role }, ct);
                if (status != HttpStatusCode.OK) throw new CliException($"Not changed: {ConsoleClient.ErrorText(body, status)}");
                env.Out.WriteLine($"{email} is now {role} here.");
                return 0;
            }
            (status, body) = await client.SendAsync(HttpMethod.Post, "invites", new { email, role, expiresInHours = 168, requireName = false, requireTwoFactor = false }, ct);
            if (status != HttpStatusCode.OK) throw new CliException($"Not invited: {ConsoleClient.ErrorText(body, status)}");
            env.Out.WriteLine($"Invited {email} as {role}; the invite lasts 7 days.");
            Link(body, "They accept it with this link");
            return 0;
        }

        /// <summary>A link to pass on by hand, unless the server emailed it.</summary>
        private void Link(JsonObject body, string? what)
        {
            if (body["emailed"]?.GetValue<bool>() == true) { env.Out.WriteLine("They've been emailed."); return; }
            if (what is not null && body["link"]?.ToString() is { Length: > 0 } link) env.Out.WriteLine($"{what} (the server can't send email, so pass it on): {link}");
        }

        // ----- service accounts --------------------------------------------------------------------

        private async Task<(ConsoleClient Client, JsonObject Body)> AccountsAsync(CancellationToken ct)
        {
            var client = await ClientAsync(ct);
            var (status, body) = await client.SendAsync(HttpMethod.Get, "service-accounts", null, ct);
            if (status != HttpStatusCode.OK) throw new CliException(ConsoleClient.ErrorText(body, status));
            return (client, body);
        }

        /// <summary>The account named (or with the id) <paramref name="which"/>.</summary>
        private static JsonNode Account(JsonObject body, string which) =>
            body["accounts"]?.AsArray().FirstOrDefault(a => a?["name"]?.GetValue<string>() == which || a?["id"]?.GetValue<string>() == which)
            ?? throw new CliException($"No service account {which}. See them: winche accounts");

        /// <summary><c>winche accounts</c>: each account, its role or scopes, keys and trusted identities.</summary>
        public async Task<int> AccountsListAsync(CancellationToken ct)
        {
            var (_, body) = await AccountsAsync(ct);
            var accounts = body["accounts"]?.AsArray() ?? [];
            if (accounts.Count == 0) env.Out.WriteLine("No service accounts. Make one: winche accounts:create <name> --scopes functions,rules (CI) or --role member (a server)");
            foreach (var a in accounts)
            {
                var what = a!["role"]?.GetValue<string>() is { } role ? $"role {role.ToLowerInvariant()}" : $"deploys {string.Join(",", a["scopes"]!.AsArray())}";
                var used = a["lastUsedAt"]?.GetValue<DateTimeOffset>() is { } at ? $"used {at:yyyy-MM-dd}" : "never used";
                env.Out.WriteLine($"{a["name"]}  ({what}; by {a["createdBy"]}; {used}{(a["disabled"]?.GetValue<bool>() == true ? "; turned off" : "")})");
                if (a["makerIsAdmin"]?.GetValue<bool>() == false) env.Out.WriteLine($"  ! {a["createdBy"]} is no longer an admin here: check it's still needed, and replace its key if they had it");
                foreach (var k in a["keys"]!.AsArray())
                    env.Out.WriteLine($"  key {k!["id"]}  until {k["expiresAt"]!.GetValue<DateTimeOffset>():yyyy-MM-dd}, " +
                        (k["lastUsedAt"]?.GetValue<DateTimeOffset>() is { } ku ? $"used {ku:yyyy-MM-dd}{(k["lastUsedFrom"]?.GetValue<string>() is { } from ? $" from {from}" : "")}" : "never used") +
                        (k["unused"]?.GetValue<bool>() == true ? " (unused 90 days)" : ""));
                foreach (var i in a["identities"]!.AsArray())
                    env.Out.WriteLine($"  trusts {i!["id"]}  {i["issuer"]}, subject {i["subject"]}, audience {i["audience"]}");
            }
            return 0;
        }

        /// <summary><c>winche accounts:create &lt;name&gt; --role viewer|member|admin | --scopes functions,rules,…</c>.</summary>
        public async Task<int> AccountsCreateAsync(string name, CancellationToken ct)
        {
            var role = args.Value("role");
            var scopes = args.Value("scopes")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
            if ((role is null) == (scopes.Length == 0))
                throw new CliException("Give it --role viewer|member|admin (a server using the data) or --scopes functions,rules,indexes,assets (CI that deploys).", 2);
            var (status, body) = await (await ClientAsync(ct)).SendAsync(HttpMethod.Post, "service-accounts", new { name, role, scopes }, ct);
            if (status != HttpStatusCode.OK) throw new CliException($"Not made: {ConsoleClient.ErrorText(body, status)}");
            env.Out.WriteLine($"Made the service account {name}. Give it a key (winche accounts:keys:add {name}) or trust its CI's identity (winche accounts:trust {name} --issuer … --subject … --audience …).");
            return 0;
        }

        /// <summary><c>winche accounts:keys:add &lt;account&gt; [--days 90] [--quiet]</c>: the key, shown once.</summary>
        public async Task<int> AccountsKeysAddAsync(string which, CancellationToken ct)
        {
            var days = args.Value("days") is { } d ? (int.TryParse(d, out var n) ? n : throw new CliException($"--days {d} isn't a number.", 2)) : 90;
            var (client, list) = await AccountsAsync(ct);
            var account = Account(list, which);
            var (status, body) = await client.SendAsync(HttpMethod.Post, $"service-accounts/{account["id"]}/keys", new { days }, ct);
            if (status != HttpStatusCode.OK) throw new CliException($"Not made: {ConsoleClient.ErrorText(body, status)}");
            if (args.Flag("quiet")) { env.Out.WriteLine(body["secret"]); return 0; }
            env.Out.WriteLine($"A key for {account["name"]}, for {days} days. It's shown once:");
            env.Out.WriteLine(body["secret"]);
            env.Out.WriteLine("Put it in the secrets of what uses it: WINCHE_TOKEN for the CLI, or initializeAdmin({ key }) for @winche-technologies/winche-admin.");
            return 0;
        }

        /// <summary><c>winche accounts:keys:revoke &lt;account&gt; &lt;key id&gt;</c>.</summary>
        public async Task<int> AccountsKeysRevokeAsync(string which, string keyId, CancellationToken ct)
        {
            var (client, list) = await AccountsAsync(ct);
            var account = Account(list, which);
            var (status, body) = await client.SendAsync(HttpMethod.Delete, $"service-accounts/{account["id"]}/keys/{Uri.EscapeDataString(keyId)}", null, ct);
            if (status != HttpStatusCode.NoContent) throw new CliException($"Not revoked: {ConsoleClient.ErrorText(body, status)}");
            env.Out.WriteLine($"Revoked the key {keyId} of {account["name"]}.");
            return 0;
        }

        /// <summary><c>winche accounts:trust &lt;account&gt; --issuer &lt;url&gt; --subject &lt;pattern&gt; --audience &lt;aud&gt;</c>.</summary>
        public async Task<int> AccountsTrustAsync(string which, CancellationToken ct)
        {
            string Need(string flag) => args.Value(flag) ?? throw new CliException($"Give --{flag}.", 2);
            var (client, list) = await AccountsAsync(ct);
            var account = Account(list, which);
            var (status, body) = await client.SendAsync(HttpMethod.Post, $"service-accounts/{account["id"]}/identities",
                new { issuer = Need("issuer"), subject = Need("subject"), audience = Need("audience") }, ct);
            if (status != HttpStatusCode.OK) throw new CliException($"Not trusted: {ConsoleClient.ErrorText(body, status)}");
            env.Out.WriteLine($"{account["name"]} trusts tokens from {args.Value("issuer")} for {args.Value("subject")}: set WINCHE_ID_TOKEN to the job's token (audience {args.Value("audience")}).");
            return 0;
        }

        /// <summary><c>winche accounts:untrust &lt;account&gt; &lt;identity id&gt;</c>.</summary>
        public async Task<int> AccountsUntrustAsync(string which, string identityId, CancellationToken ct)
        {
            var (client, list) = await AccountsAsync(ct);
            var account = Account(list, which);
            var (status, body) = await client.SendAsync(HttpMethod.Delete, $"service-accounts/{account["id"]}/identities/{Uri.EscapeDataString(identityId)}", null, ct);
            if (status != HttpStatusCode.NoContent) throw new CliException($"Not removed: {ConsoleClient.ErrorText(body, status)}");
            env.Out.WriteLine($"{account["name"]} no longer trusts {identityId}.");
            return 0;
        }

        /// <summary><c>winche accounts:disable | accounts:enable &lt;account&gt;</c>.</summary>
        public async Task<int> AccountsSwitchAsync(string which, bool disabled, CancellationToken ct)
        {
            var (client, list) = await AccountsAsync(ct);
            var account = Account(list, which);
            var (status, body) = await client.SendAsync(HttpMethod.Put, $"service-accounts/{account["id"]}",
                new { role = account["role"]?.GetValue<string>(), scopes = account["scopes"]!.AsArray().Select(s => s!.GetValue<string>()).ToArray(), disabled }, ct);
            if (status != HttpStatusCode.NoContent) throw new CliException($"Not changed: {ConsoleClient.ErrorText(body, status)}");
            env.Out.WriteLine(disabled ? $"Turned {account["name"]} off: nothing can sign in as it." : $"Turned {account["name"]} on.");
            return 0;
        }

        /// <summary><c>winche accounts:delete &lt;account&gt;</c>.</summary>
        public async Task<int> AccountsDeleteAsync(string which, CancellationToken ct)
        {
            var (client, list) = await AccountsAsync(ct);
            var account = Account(list, which);
            var (status, body) = await client.SendAsync(HttpMethod.Delete, $"service-accounts/{account["id"]}", null, ct);
            if (status != HttpStatusCode.NoContent) throw new CliException($"Not deleted: {ConsoleClient.ErrorText(body, status)}");
            env.Out.WriteLine($"Deleted the service account {account["name"]}, its keys and trusted identities.");
            return 0;
        }

        // ----- settings files ----------------------------------------------------------------------

        /// <summary>
        /// A settings area kept as <c>settings/&lt;name&gt;.json</c> beside winche.json. <paramref name="Elsewhere"/>: keys kept
        /// in another file (the database's indexes and expiry are database.indexes), left as they are on the server.
        /// </summary>
        private sealed record SettingsFile(string Name, string Path, string Description, params string[] Elsewhere);

        private static readonly SettingsFile[] SettingsFiles =
        [
            new("database", "settings/database", "documents: limits, history, request logs", "indexes", "ttl", "dropUndeclaredIndexes"),
            new("database.live", "settings/database.live", "live connections' limits"),
            new("storage", "settings/storage", "files: size limit, request logs"),
            new("auth", "auth/settings/signin", "your apps' sign-in methods, providers and emails (the SMTP password isn't in it)"),
            new("auth.providers", "auth/settings/providers", "trusted token issuers"),
            new("webhooks", "hooks", "each product's webhooks (their signing secrets stay on the server)"),
        ];

        private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private IReadOnlyList<SettingsFile> SelectedSettings(IReadOnlyList<SettingsFile> files)
        {
            if (args.Value("only") is not { } only) return files;
            var wanted = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (wanted.FirstOrDefault(w => files.All(f => f.Name != w)) is { } unknown)
                throw new CliException($"--only {unknown}: settings files are {string.Join(", ", files.Select(f => f.Name))}.", 2);
            return [.. files.Where(f => wanted.Contains(f.Name))];
        }

        /// <summary><c>settings/</c> beside winche.json (or here, for the server's own settings without a project).</summary>
        private string SettingsDirectory => Path.Combine(Config?.FilePath is { } config ? Path.GetDirectoryName(config)! : env.WorkingDirectory, "settings");

        private string SettingsPath(SettingsFile f) => Path.Combine(SettingsDirectory, f.Name + ".json");

        /// <summary>The server's settings for <paramref name="f"/> (without what's kept elsewhere) and their version; null when it has none.</summary>
        private static async Task<(JsonObject Settings, JsonObject Full, int Version)?> LiveAsync(ConsoleClient client, SettingsFile f, CancellationToken ct)
        {
            var (status, body) = await client.SendAsync(HttpMethod.Get, f.Path, null, ct);
            if (status == HttpStatusCode.NotFound) return null;
            if (status != HttpStatusCode.OK) throw new CliException($"{f.Name}: {ConsoleClient.ErrorText(body, status)}");
            if (body["available"]?.GetValue<bool>() == false || body["settings"] is not JsonObject settings) return null;
            var shown = (JsonObject)settings.DeepClone();
            foreach (var key in f.Elsewhere) shown.Remove(key);
            return (shown, settings, body["version"]?["number"]?.GetValue<int>() ?? 0);
        }

        private static string Text(JsonNode node) => node.ToJsonString(Pretty) + "\n";

        /// <summary><c>winche settings pull</c>: writes the project's settings into <c>settings/*.json</c>.</summary>
        public async Task<int> SettingsPullAsync(CancellationToken ct) { RequireConfig(); return await PullAsync(await ClientAsync(ct), SettingsFiles, ct); }

        private async Task<int> PullAsync(ConsoleClient client, IReadOnlyList<SettingsFile> files, CancellationToken ct)
        {
            Directory.CreateDirectory(SettingsDirectory);
            foreach (var f in SelectedSettings(files))
            {
                if (await LiveAsync(client, f, ct) is not { } live)
                {
                    env.Out.WriteLine($"  {f.Name}: not here (its product isn't started, or the app's code sets it)");
                    continue;
                }
                File.WriteAllText(SettingsPath(f), Text(live.Settings));
                env.Out.WriteLine($"  wrote settings/{f.Name}.json (v{live.Version}): {f.Description}");
            }
            return 0;
        }

        /// <summary><c>winche settings diff</c>: how the files differ from the project's settings.</summary>
        public async Task<int> SettingsDiffAsync(CancellationToken ct) { RequireConfig(); return await DiffAsync(await ClientAsync(ct), SettingsFiles, ct); }

        private async Task<int> DiffAsync(ConsoleClient client, IReadOnlyList<SettingsFile> files, CancellationToken ct)
        {
            var any = false;
            foreach (var (f, file, live) in await LocalAndLiveAsync(client, files, ct))
            {
                var before = live is null ? "" : Text(live.Value.Settings);
                var after = Text(file);
                if (before == after) { env.Out.WriteLine($"{f.Name}: unchanged"); continue; }
                any = true;
                env.Out.WriteLine($"{f.Name}:");
                WriteDiff(before, after, live is null ? "(not on the server)" : $"v{live.Value.Version} (on the server)", $"settings/{f.Name}.json");
            }
            return any ? 1 : 0;
        }

        /// <summary><c>winche settings deploy -m "why"</c>: saves changed files as new versions (with the note in the history).</summary>
        public async Task<int> SettingsDeployAsync(CancellationToken ct) { RequireConfig(); return await DeployAsync(await ClientAsync(ct), SettingsFiles, ct); }

        private async Task<int> DeployAsync(ConsoleClient client, IReadOnlyList<SettingsFile> files, CancellationToken ct)
        {
            var note = args.Value("message") ?? DefaultNote().Replace("winche rules deploy", "winche settings deploy");
            var changed = 0;
            foreach (var (f, file, live) in await LocalAndLiveAsync(client, files, ct))
            {
                if (live is null) throw new CliException($"{f.Name}: the project has none to change (its product isn't started, or the app's code sets it).");
                if (Text(live.Value.Settings) == Text(file) && !args.Flag("force")) { env.Out.WriteLine($"  {f.Name}: unchanged"); continue; }
                if (args.Flag("dry-run"))
                {
                    env.Out.WriteLine($"{f.Name} would change:");
                    WriteDiff(Text(live.Value.Settings), Text(file), $"v{live.Value.Version}", $"settings/{f.Name}.json");
                    continue;
                }
                var settings = (JsonObject)file.DeepClone();
                foreach (var key in f.Elsewhere)
                    if (live.Value.Full[key] is { } kept) settings[key] = kept.DeepClone();
                var (status, body) = await client.SendAsync(HttpMethod.Put, f.Path, new { settings, reason = note, basedOn = live.Value.Version }, ct);
                if (status == HttpStatusCode.Conflict)
                    throw new CliException($"{f.Name}: changed on the server while deploying. See: winche settings diff");
                if (status != HttpStatusCode.OK) throw new CliException($"{f.Name}: {ConsoleClient.ErrorText(body, status)}");
                env.Out.WriteLine($"  deployed settings/{f.Name}.json as v{body["number"]}");
                changed++;
            }
            if (!args.Flag("dry-run")) env.Out.WriteLine(changed == 0 ? "Nothing to deploy." : $"Deployed {changed} settings file{(changed == 1 ? "" : "s")} (\"{note}\").");
            return 0;
        }

        /// <summary>Each selected settings file that exists here, read, with the server's.</summary>
        private async Task<List<(SettingsFile File, JsonObject Local, (JsonObject Settings, JsonObject Full, int Version)? Live)>> LocalAndLiveAsync(ConsoleClient client, IReadOnlyList<SettingsFile> files, CancellationToken ct)
        {
            var list = new List<(SettingsFile, JsonObject, (JsonObject, JsonObject, int)?)>();
            foreach (var f in SelectedSettings(files))
            {
                var path = SettingsPath(f);
                if (!File.Exists(path))
                {
                    if (args.Value("only") is not null) throw new CliException($"No settings/{f.Name}.json. Run: winche settings pull --only {f.Name}");
                    continue;
                }
                JsonObject local;
                try { local = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new CliException($"settings/{f.Name}.json isn't a JSON object."); }
                catch (JsonException ex) { throw new CliException($"settings/{f.Name}.json isn't valid JSON: {ex.Message}"); }
                foreach (var key in f.Elsewhere)
                    if (local.ContainsKey(key)) throw new CliException($"settings/{f.Name}.json: \"{key}\" is kept in database.indexes (winche indexes deploy), not here.");
                list.Add((f, local, await LiveAsync(client, f, ct)));
            }
            if (list.Count == 0) throw new CliException("No settings files here. Run: winche settings pull");
            return list;
        }
    }
}
