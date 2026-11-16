using System.Net;
using System.Text.Json.Nodes;

namespace Winche.Cli;

/// <summary>
/// The rest of what the console does, for operators: taking things back (members, invites, apps, projects), secrets,
/// the server's own settings and accounts, moving files and data, your apps' users, approving another CLI, and logs.
/// </summary>
internal static partial class CliApp
{
    internal sealed partial class Run
    {
        // ----- helpers -----------------------------------------------------------------------------

        /// <summary>Asks before something that can't be undone; <c>--yes</c> skips the question (and is needed without a terminal).</summary>
        private void Confirm(string question)
        {
            if (args.Flag("yes")) return;
            if (!env.Interactive) throw new CliException($"{question} Pass --yes to go ahead without a prompt.", 2);
            env.Out.Write($"{question} [y/N] ");
            var answer = env.In.ReadLine()?.Trim();
            if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
                throw new CliException("Nothing was changed.", 1);
        }

        private static async Task<JsonObject> OkAsync(Task<(HttpStatusCode Status, JsonObject Body)> call, string failure, params HttpStatusCode[] ok)
        {
            var (status, body) = await call;
            if (!(ok.Length == 0 ? (int)status is >= 200 and < 300 : ok.Contains(status)))
                throw new CliException($"{failure}: {ConsoleClient.ErrorText(body, status)}");
            return body;
        }

        private static bool NoMemberList(HttpStatusCode s) => s is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed;

        // ----- taking things back --------------------------------------------------------------------

        /// <summary>
        /// <c>winche members:remove &lt;email&gt;</c>: off a created project's members; in the first project, the account's
        /// role is taken away (or its pending invite revoked). The account itself stays: other projects may have it.
        /// </summary>
        public async Task<int> MembersRemoveAsync(string? email, CancellationToken ct)
        {
            email = Ask("Their email", email, option: "the email: winche members:remove <email>");
            var client = await ClientAsync(ct);
            var (status, body) = await client.SendAsync(HttpMethod.Get, "api/members", null, ct);
            if (status == HttpStatusCode.OK)
            {
                var member = body["members"]?.AsArray().FirstOrDefault(m => Same(m?["email"], email))
                    ?? throw new CliException(body["serverAdmins"]?.AsArray().Any(a => Same(a?["email"], email)) == true
                        ? $"{email} is a server admin: an admin of every project (winche server users to change that)."
                        : $"{email} isn't a member here. See: winche members");
                await OkAsync(client.SendAsync(HttpMethod.Delete, $"api/members/{member["userId"]}", null, ct), "Not removed");
                env.Out.WriteLine($"Removed {email} from the project.");
                return 0;
            }
            if (!NoMemberList(status)) throw new CliException(ConsoleClient.ErrorText(body, status));

            // The first project: an invite, or the account's own role.
            if ((await client.GetListAsync("api/invites", ct)).FirstOrDefault(i => Same(i?["email"], email) && i?["status"]?.ToString() == "pending") is { } invite)
            {
                await OkAsync(client.SendAsync(HttpMethod.Delete, $"api/invites/{invite["id"]}", null, ct), "Not revoked");
                env.Out.WriteLine($"Revoked {email}'s invite.");
                return 0;
            }
            var user = (await client.GetListAsync("api/users", ct)).FirstOrDefault(u => Same(u?["email"], email) && u?["role"] is not null)
                ?? throw new CliException($"{email} has no access here. See: winche members");
            if (user["role"]?.ToString() == "Admin") Confirm($"{email} is a server admin: taking the role away removes that too. Go ahead?");
            await OkAsync(client.SendAsync(HttpMethod.Put, $"api/users/{user["id"]}", new { role = "none" }, ct), "Not removed");
            env.Out.WriteLine($"{email} no longer has a role here (their account stays, with any other projects).");
            return 0;
        }

        private static bool Same(JsonNode? a, string b) => string.Equals(a?.ToString(), b, StringComparison.OrdinalIgnoreCase);

        /// <summary><c>winche invites</c>: the first project's pending invites.</summary>
        public async Task<int> InvitesAsync(CancellationToken ct)
        {
            var invites = (await (await ClientAsync(ct)).GetListAsync("api/invites", ct)).Where(i => i?["status"]?.ToString() == "pending").ToList();
            if (invites.Count == 0) env.Out.WriteLine("No pending invites.");
            foreach (var i in invites) env.Out.WriteLine($"  {i!["email"],-32} {i["role"],-8} until {i["expiresAt"]!.GetValue<DateTimeOffset>():yyyy-MM-dd}");
            return 0;
        }

        /// <summary><c>winche invites:revoke &lt;email&gt;</c>.</summary>
        public async Task<int> InvitesRevokeAsync(string? email, CancellationToken ct)
        {
            email = Ask("Their email", email, option: "the email: winche invites:revoke <email>");
            var client = await ClientAsync(ct);
            var invite = (await client.GetListAsync("api/invites", ct)).FirstOrDefault(i => Same(i?["email"], email) && i?["status"]?.ToString() == "pending")
                ?? throw new CliException($"No pending invite for {email}. See: winche invites");
            await OkAsync(client.SendAsync(HttpMethod.Delete, $"api/invites/{invite["id"]}", null, ct), "Not revoked");
            env.Out.WriteLine($"Revoked {email}'s invite.");
            return 0;
        }

        /// <summary><c>winche apps:delete &lt;id&gt;</c>.</summary>
        public async Task<int> AppsDeleteAsync(string? id, CancellationToken ct)
        {
            id = Ask("App id", id, option: "the id: winche apps:delete <id> (see winche apps)");
            Confirm($"Remove the app {id}? Its options stop identifying it to the server.");
            var (status, body) = await (await ClientAsync(ct)).SendAsync(HttpMethod.Delete, $"api/apps/registered/{Uri.EscapeDataString(id)}", null, ct);
            if (status == HttpStatusCode.NotFound) throw new CliException($"No app {id}. See: winche apps");
            if (status != HttpStatusCode.NoContent) throw new CliException($"Not removed: {ConsoleClient.ErrorText(body, status)}");
            env.Out.WriteLine($"Removed the app {id}.");
            return 0;
        }

        /// <summary><c>winche projects:archive | restore | delete &lt;id&gt;</c> (server admins). Deleting needs it archived first.</summary>
        public async Task<int> ProjectsChangeAsync(string action, string? id, CancellationToken ct)
        {
            id = Ask("Project id", id, option: $"the id: winche projects:{action} <id>");
            var client = await ClientAsync(ServerConsole(), ct);
            switch (action)
            {
                case "archive":
                    Confirm($"Archive {id}? Its apps and console are refused until it's restored; nothing is deleted.");
                    await OkAsync(client.SendAsync(HttpMethod.Post, $"api/projects/{id}/archive", new { }, ct), "Not archived");
                    env.Out.WriteLine($"Archived {id}. Bring it back with: winche projects:restore {id}");
                    break;
                case "restore":
                    await OkAsync(client.SendAsync(HttpMethod.Post, $"api/projects/{id}/restore", new { }, ct), "Not restored");
                    env.Out.WriteLine($"Restored {id}.");
                    break;
                default:
                    if (!args.Flag("yes"))
                    {
                        if (!env.Interactive) throw new CliException($"Deleting {id} removes its database and files for good. Pass --yes to go ahead.", 2);
                        env.Out.Write($"Deleting {id} removes its database and files for good. Type its id to go ahead: ");
                        if (env.In.ReadLine()?.Trim() != id) throw new CliException("Nothing was deleted.", 1);
                    }
                    await OkAsync(client.SendAsync(HttpMethod.Delete, $"api/projects/{id}", null, ct), "Not deleted");
                    env.Out.WriteLine($"Deleted {id}, with its database and files.");
                    break;
            }
            return 0;
        }

        // ----- secrets ---------------------------------------------------------------------------------

        /// <summary>A secret's value: <c>WINCHE_SECRET</c>, or typed without being shown.</summary>
        private string SecretValue(string what) => AskSecret(what, "WINCHE_SECRET", confirm: false);

        // ----- function secrets ------------------------------------------------------------------

        /// <summary><c>winche functions secrets</c>: names, whether set, by whom, and which functions declare each. Never values.</summary>
        public async Task<int> FunctionSecretsAsync(CancellationToken ct)
        {
            var client = await ClientAsync(ct);
            var body = await OkAsync(client.SendAsync(HttpMethod.Get, "api/functions/secrets", null, ct), "Can't read the function secrets");
            var list = body["secrets"]?.AsArray() ?? [];
            if (body["available"]?.GetValue<bool>() == false) env.Out.WriteLine("Secrets need the server's master key: none can be set here.");
            if (list.Count == 0) env.Out.WriteLine("No function secrets yet.");
            foreach (var s in list)
            {
                var set = s!["isSet"]?.GetValue<bool>() == true
                    ? $"set {s["setAt"]!.GetValue<DateTimeOffset>():yyyy-MM-dd}{(s["setBy"] is { } by ? $" by {by}" : "")}" : "NOT SET";
                var used = s["usedBy"]?.AsArray().Select(u => u!.ToString()).ToList() ?? [];
                env.Out.WriteLine($"  {s["name"],-28} {set,-36} {(used.Count == 0 ? "no function declares it" : "used by " + string.Join(", ", used))}");
            }
            env.Out.WriteLine("Add or replace one: winche functions secrets:set <NAME>   Remove one: winche functions secrets:remove <NAME>");
            return 0;
        }

        /// <summary>
        /// <c>winche functions secrets:set NAME</c>: adds or replaces it. The value comes from <c>--from-file</c> (e.g. a
        /// service account's JSON), else WINCHE_SECRET, else it's typed without being shown.
        /// </summary>
        public async Task<int> FunctionSecretSetAsync(string name, CancellationToken ct)
        {
            CheckSecretName(name);
            string value;
            if (args.Value("from-file") is { } file)
            {
                var path = Path.GetFullPath(file, env.WorkingDirectory);
                if (!File.Exists(path)) throw new CliException($"There's no file {file}.", 2);
                value = await File.ReadAllTextAsync(path, ct);
            }
            else value = env.GetVariable("WINCHE_SECRET") ?? (env.Interactive ? SecretValue(name) : throw new CliException("Set WINCHE_SECRET or use --from-file: there's no terminal to ask in.", 2));
            if (value.Length == 0) throw new CliException("The value is empty. To remove the secret: winche functions secrets:remove " + name, 2);
            var client = await ClientAsync(ct);
            await OkAsync(client.SendAsync(HttpMethod.Put, $"api/functions/secrets/{name}", new { value }, ct), "Not set");
            env.Out.WriteLine($"Set {name}. Functions get it from their next run.");
            return 0;
        }

        /// <summary><c>winche functions secrets:remove NAME</c>.</summary>
        public async Task<int> FunctionSecretRemoveAsync(string name, CancellationToken ct)
        {
            CheckSecretName(name);
            var client = await ClientAsync(ct);
            var (status, body) = await client.SendAsync(HttpMethod.Delete, $"api/functions/secrets/{name}", null, ct);
            if (status == HttpStatusCode.NotFound) throw new CliException($"There's no secret {name}. See: winche functions secrets");
            if ((int)status is < 200 or >= 300) throw new CliException($"Not removed: {ConsoleClient.ErrorText(body, status)}");
            env.Out.WriteLine($"Removed {name}. Functions that read it fail until it's set again.");
            return 0;
        }

        private static void CheckSecretName(string name)
        {
            if (Winche.Functions.FunctionSecrets.Problem(name) is { } problem) throw new CliException(problem, 2);
        }

        /// <summary>
        /// <c>winche secrets</c> / <c>secrets:set smtp-password | keycloak-sync --provider &lt;name&gt;</c>: the project's
        /// sign-in secrets (your apps' email, a provider's Keycloak sync), set without being shown or saved anywhere else.
        /// </summary>
        public async Task<int> SecretsAsync(string? name, CancellationToken ct) => await SignInSecretsAsync(await ClientAsync(ct), "api/app-auth", name, ct);

        /// <summary>The sign-in secrets of a Winche Auth (a project's at api/app-auth, the console accounts' at api/server/auth).</summary>
        private async Task<int> SignInSecretsAsync(ConsoleClient client, string root, string? name, CancellationToken ct, bool listOnly = false)
        {
            var signin = await OkAsync(client.SendAsync(HttpMethod.Get, $"{root}/settings/signin", null, ct), "Can't read the sign-in settings");
            if (signin["available"]?.GetValue<bool>() == false) throw new CliException("This project has no sign-in settings (start Authentication: winche products:start authentication).");
            if (name is null)
            {
                env.Out.WriteLine($"  smtp-password              {Described(signin["smtpPassword"])}");
                foreach (var (provider, info) in signin["syncSecrets"]?.AsObject() ?? [])
                    env.Out.WriteLine($"  keycloak-sync --provider {provider,-12} {Described(info)}");
                if (!listOnly) env.Out.WriteLine("Set one: winche secrets:set <name> (the value from WINCHE_SECRET, or typed)");
                return 0;
            }
            object body = name switch
            {
                "smtp-password" => new { settings = signin["settings"], smtpPassword = SecretValue("SMTP password"), reason = Reason("Set the SMTP password"), basedOn = Version(signin) },
                "keycloak-sync" => new
                {
                    settings = signin["settings"], reason = Reason($"Set {args.Value("provider")}'s Keycloak sync secret"), basedOn = Version(signin),
                    syncSecrets = new Dictionary<string, string> { [args.Value("provider") ?? throw new CliException("--provider <name>: whose Keycloak sync (see winche secrets).", 2)] = SecretValue("Keycloak client secret") },
                },
                _ => throw new CliException($"'{name}' isn't a secret here: smtp-password, keycloak-sync --provider <name>.", 2),
            };
            await OkAsync(client.SendAsync(HttpMethod.Put, $"{root}/settings/signin", body, ct), "Not set");
            env.Out.WriteLine($"Set {name}{(name == "keycloak-sync" ? $" for {args.Value("provider")}" : "")}.");
            return 0;
        }

        private static string Described(JsonNode? info) => info is JsonObject o && o["setAt"] is { } at
            ? $"set {at.GetValue<DateTimeOffset>():yyyy-MM-dd} by {o["setBy"]}" : "not set";

        private string Reason(string fallback) => args.Value("message") ?? fallback;

        private static int Version(JsonObject body) => body["version"]?["number"]?.GetValue<int>() ?? 0;

        /// <summary><c>winche webhooks:secret &lt;id&gt; [--rotate]</c>: the signing secret receivers check signatures with.</summary>
        public async Task<int> WebhookSecretAsync(string? id, CancellationToken ct)
        {
            id = Ask("Webhook id", id, option: "the id: winche webhooks:secret <id> (see settings/webhooks.json)");
            var client = await ClientAsync(ct);
            if (args.Flag("rotate"))
            {
                Confirm($"Make a new signing secret for {id}? Receivers need it before they accept its calls.");
                await OkAsync(client.SendAsync(HttpMethod.Post, $"api/hooks/{Uri.EscapeDataString(id)}/secret/rotate", new { }, ct), "Not rotated");
            }
            var (status, body) = await client.SendAsync(HttpMethod.Get, $"api/hooks/{Uri.EscapeDataString(id)}/secret", null, ct);
            if (status == HttpStatusCode.NotFound) throw new CliException($"No webhook {id} (it gets a secret when first saved).");
            if (status != HttpStatusCode.OK) throw new CliException(ConsoleClient.ErrorText(body, status));
            env.Out.WriteLine(body["secret"]);
            return 0;
        }

        // ----- the server's own settings, secrets and accounts (server admins) -------------------------

        private static readonly SettingsFile[] ServerFiles =
        [
            new("server", "api/server/settings", "the console's sign-in providers, email and the server's own settings"),
            new("server.auth", "api/server/auth/settings/signin", "how the console's accounts sign in: passwords, two-step, sessions, lockout"),
        ];

        /// <summary><c>winche server settings pull | diff | deploy</c>: the server's settings as files, like a project's.</summary>
        public async Task<int> ServerSettingsAsync(string verb, CancellationToken ct)
        {
            var client = await ClientAsync(ServerConsole(), ct);
            return verb switch
            {
                "pull" => await PullAsync(client, ServerFiles, ct),
                "diff" => await DiffAsync(client, ServerFiles, ct),
                "deploy" => await DeployAsync(client, ServerFiles, ct),
                _ => throw new CliException("Usage: winche server settings pull | diff | deploy", 2),
            };
        }

        /// <summary><c>winche server secrets</c> / <c>server secrets:set &lt;name&gt;</c>: the server's write-only secrets.</summary>
        public async Task<int> ServerSecretsAsync(string? name, CancellationToken ct)
        {
            var client = await ClientAsync(ServerConsole(), ct);
            // The console accounts' email and Keycloak sync, like a project's; then the server's own (providers' client secrets).
            if (name is "smtp-password" or "keycloak-sync") return await SignInSecretsAsync(client, "api/server/auth", name, ct);
            var settings = await OkAsync(client.SendAsync(HttpMethod.Get, "api/server/settings", null, ct), "Can't read the server's settings");
            var secrets = settings["secrets"]?.AsArray() ?? [];
            if (name is null)
            {
                await SignInSecretsAsync(client, "api/server/auth", null, ct, listOnly: true);
                foreach (var s in secrets) env.Out.WriteLine($"  {s!["name"],-36} {(s["setAt"] is null ? "not set" : $"set {s["setAt"]!.GetValue<DateTimeOffset>():yyyy-MM-dd} by {s["setBy"]}")}  {s["about"]}");
                env.Out.WriteLine("Set one: winche server secrets:set <name> (the value from WINCHE_SECRET, or typed; empty removes it)");
                return 0;
            }
            if (!secrets.Any(s => s?["name"]?.ToString() == name)) throw new CliException($"'{name}' isn't one of the server's secrets. See: winche server secrets", 2);
            var value = env.GetVariable("WINCHE_SECRET") ?? (env.Interactive ? SecretValue(name) : throw new CliException("Set WINCHE_SECRET: there's no terminal to ask in.", 2));
            await OkAsync(client.SendAsync(HttpMethod.Put, $"api/server/secrets/{Uri.EscapeDataString(name)}", new { value }, ct), "Not set");
            env.Out.WriteLine(value.Length == 0 ? $"Removed {name}." : $"Set {name}.");
            return 0;
        }

        /// <summary>
        /// <c>winche server users</c> and <c>server users:disable | enable | sign-out &lt;email&gt;</c>: the console's accounts.
        /// Turning one off signs it out everywhere too; the last admin can't be turned off.
        /// </summary>
        public async Task<int> ServerUsersAsync(string? action, string? email, CancellationToken ct)
        {
            var client = await ClientAsync(ServerConsole(), ct);
            var users = await client.GetListAsync("api/users", ct);
            if (action is null)
            {
                foreach (var u in users)
                    env.Out.WriteLine($"  {u!["email"],-32} {u["role"]?.ToString() ?? "(no role in the first project)",-30} {(u["active"]?.GetValue<bool>() == false ? "turned off" : "")}{(u["twoFactorEnabled"]?.GetValue<bool>() == true ? " two-step" : "")}");
                return 0;
            }
            email = Ask("Their email", email, option: $"the email: winche server users:{action} <email>");
            var user = users.FirstOrDefault(u => Same(u?["email"], email)) ?? throw new CliException($"No console account {email}. See: winche server users");
            var id = user["id"]!.ToString();
            switch (action)
            {
                case "disable":
                    Confirm($"Turn {email}'s account off? They're signed out everywhere and can't sign in until it's turned back on.");
                    await OkAsync(client.SendAsync(HttpMethod.Put, $"api/users/{id}", new { active = false }, ct), "Not turned off");
                    env.Out.WriteLine($"Turned {email}'s account off.");
                    break;
                case "enable":
                    await OkAsync(client.SendAsync(HttpMethod.Put, $"api/users/{id}", new { active = true }, ct), "Not turned on");
                    env.Out.WriteLine($"Turned {email}'s account back on.");
                    break;
                case "sign-out":
                    var ended = await OkAsync(client.SendAsync(HttpMethod.Post, $"api/server/auth/users/{id}/signOut", new { }, ct), "Not signed out");
                    env.Out.WriteLine($"Signed {email} out everywhere ({ended["sessions"]} session{(ended["sessions"]?.ToString() == "1" ? "" : "s")}).");
                    break;
                default: throw new CliException("Usage: winche server users[:disable | :enable | :sign-out <email>]", 2);
            }
            return 0;
        }

        // ----- your apps' users ------------------------------------------------------------------------

        /// <summary><c>winche users [--search …]</c> and <c>users:disable | enable | sign-out &lt;email or uid&gt;</c>.</summary>
        public async Task<int> AppUsersAsync(string? action, string? who, CancellationToken ct)
        {
            var client = await ClientAsync(ct);
            if (action is null)
            {
                var query = $"api/app-auth/users?limit={args.Value("limit") ?? "50"}" + (args.Value("search") is { } s ? $"&search={Uri.EscapeDataString(s)}" : "");
                var page = await OkAsync(client.SendAsync(HttpMethod.Get, query, null, ct), "Can't list the users");
                foreach (var u in page["users"]?.AsArray() ?? [])
                    env.Out.WriteLine($"  {u!["uid"],-30} {u["email"]?.ToString() ?? (u["isAnonymous"]?.GetValue<bool>() == true ? "(guest)" : ""),-32} {(u["disabled"]?.GetValue<bool>() == true ? "disabled" : "")}");
                env.Out.WriteLine($"{page["total"]} user{(page["total"]?.ToString() == "1" ? "" : "s")}{(args.Value("search") is null ? "" : " found")}.");
                return 0;
            }
            who = Ask("Their email or uid", who, option: $"who: winche users:{action} <email or uid>");
            var uid = who;
            if (who.Contains('@'))
            {
                var found = await OkAsync(client.SendAsync(HttpMethod.Get, $"api/app-auth/users?search={Uri.EscapeDataString(who)}&limit=5", null, ct), "Can't find them");
                uid = found["users"]?.AsArray().FirstOrDefault(u => Same(u?["email"], who))?["uid"]?.ToString() ?? throw new CliException($"No user {who}.");
            }
            switch (action)
            {
                case "disable":
                case "enable":
                    await OkAsync(client.SendAsync(HttpMethod.Post, $"api/app-auth/users/{uid}/disabled", new { disabled = action == "disable" }, ct), "Not changed");
                    env.Out.WriteLine(action == "disable" ? $"Disabled {who}: signed out, and can't sign in." : $"Enabled {who}.");
                    break;
                case "sign-out":
                    await OkAsync(client.SendAsync(HttpMethod.Post, $"api/app-auth/users/{uid}/signOut", new { }, ct), "Not signed out");
                    env.Out.WriteLine($"Signed {who} out everywhere.");
                    break;
                default: throw new CliException("Usage: winche users[:disable | :enable | :sign-out <email or uid>]", 2);
            }
            return 0;
        }

        // ----- moving files and data -------------------------------------------------------------------

        /// <summary>
        /// <c>winche storage:move</c>: where the project's files are, or a move to an S3-compatible bucket (<c>--bucket</c>,
        /// <c>--region</c>, <c>--service-url</c>, <c>--access-key</c>, <c>--path-style</c>; the secret key in WINCHE_SECRET)
        /// or back to the server (<c>--to server</c>). It copies every file, then switches; <c>--finish</c> ends the move
        /// (the old copy is left as it is), <c>--cancel</c> stops one before the switch.
        /// </summary>
        public async Task<int> StorageMoveAsync(CancellationToken ct)
        {
            var client = await ClientAsync(ct);
            const string path = "api/settings/storage/location";
            var state = await OkAsync(client.SendAsync(HttpMethod.Get, path, null, ct), "Can't read where the files are");
            var reason = Reason("winche storage:move");
            if (args.Flag("finish") || args.Flag("cancel"))
            {
                var step = args.Flag("finish") ? "finish" : "cancel";
                await OkAsync(client.SendAsync(HttpMethod.Post, $"{path}/move/{step}", new { reason }, ct), $"Not {(step == "finish" ? "finished" : "cancelled")}");
                env.Out.WriteLine(step == "finish" ? "The move is finished." : "The move is cancelled; files stay where they were.");
                return 0;
            }
            var to = args.Value("to");
            if (to is null && args.Value("bucket") is null)
            {
                env.Out.WriteLine($"Where files are kept: {Mid(state["activeLabel"])}.");
                if (state["move"] is JsonObject m)
                    env.Out.WriteLine(m["phase"]?.ToString() == "switched"
                        ? $"Moved there from {Mid(m["fromLabel"])}; the move ends with: winche storage:move --finish"
                        : $"A move to {Mid(m["toLabel"])} is {m["phase"]} (winche storage:move --cancel stops it).");
                env.Out.WriteLine("Move them: winche storage:move --bucket <name> --region <region> [--service-url …] --access-key … (WINCHE_SECRET: the secret key), or --to server");
                return 0;
            }
            if (state["move"] is JsonObject { } open && open["phase"]?.ToString() != "switched")
                throw new CliException($"A move to {open["toLabel"]} is already {open["phase"]}: wait, or winche storage:move --cancel.");
            object location; string? secret = null;
            if (to is "server" or "code") location = new { kind = "code" };
            else
            {
                location = new
                {
                    kind = "s3",
                    s3 = new
                    {
                        bucket = args.Value("bucket") ?? throw new CliException("--bucket: the bucket's name.", 2),
                        region = args.Value("region") ?? "", serviceUrl = args.Value("service-url"), accessKey = args.Value("access-key"),
                        forcePathStyle = args.Flag("path-style"),
                    },
                };
                secret = SecretValue("The bucket's secret key");
            }
            Confirm("Copy every file there, then switch to it?");
            await OkAsync(client.SendAsync(HttpMethod.Post, $"{path}/move", new { location, secretKey = secret, reason }, ct), "Not moved");
            env.Out.WriteLine("Copying the files…");
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), env.Time, ct);
                state = await OkAsync(client.SendAsync(HttpMethod.Get, path, null, ct), "Can't follow the move");
                var p = state["progress"];
                if (p is not null) env.Out.WriteLine($"  {p["copied"]?.GetValue<int>() + p["alreadyThere"]?.GetValue<int>()}/{p["total"]} copied{(p["failed"]?.GetValue<int>() > 0 ? $", {p["failed"]} failed: {p["lastError"]}" : "")}");
                var phase = state["move"]?["phase"]?.ToString();
                if (phase == "ready") break;
                if (phase is null) throw new CliException("The move stopped (cancelled?). See: winche storage:move");
            }
            await OkAsync(client.SendAsync(HttpMethod.Post, $"{path}/move/switch", new { reason }, ct), "Not switched");
            env.Out.WriteLine("Every file is copied; the project now uses the new location. When you're happy: winche storage:move --finish");
            return 0;
        }

        /// <summary>A location's label inside a sentence ("On this server's disk" → "this server's disk").</summary>
        private static string Mid(JsonNode? label) => label?.ToString() is { Length: > 3 } l && l.StartsWith("On ", StringComparison.Ordinal) ? l[3..] : label?.ToString() ?? "";

        /// <summary>
        /// <c>winche data:move</c> (server admins): where the project's database is, or a move to another (<c>--db-name</c>,
        /// <c>--db-host</c>, …, password in WINCHE_DB_PASSWORD). The project pauses while every table is copied and
        /// counted; <c>--finish</c> then deletes the old copy (<c>--keep-old</c> keeps it).
        /// </summary>
        public async Task<int> DataMoveAsync(CancellationToken ct)
        {
            var client = await ClientAsync(ct);
            var info = await OkAsync(client.SendAsync(HttpMethod.Get, "api/data-location", null, ct), "Can't read where the data is");
            if (args.Flag("finish"))
            {
                if (info["previous"] is null) throw new CliException("There's no move to finish.");
                var deleteOld = !args.Flag("keep-old");
                if (deleteOld) Confirm($"Delete the old copy ({info["previous"]!["database"]})?");
                await OkAsync(client.SendAsync(HttpMethod.Post, "api/data-location/finish", new { deleteOld }, ct), "Not finished");
                env.Out.WriteLine(deleteOld ? "Finished: the old copy is deleted." : "Finished: the old copy is kept, no longer used.");
                return 0;
            }
            if (DatabaseTarget() is not { } target)
            {
                env.Out.WriteLine($"The data is in {info["database"]}{(info["host"] is { } h ? $" on {h}" : " on the server's PostgreSQL")}" +
                    (info["sizeBytes"] is { } size ? $" ({size.GetValue<long>() / 1024 / 1024} MB)" : "") + ".");
                if (info["previous"] is JsonObject prev) env.Out.WriteLine($"Moved from {prev["database"]}; finish with: winche data:move --finish");
                env.Out.WriteLine("Move it: winche data:move --db-name <name> [--db-host … --db-user …] (WINCHE_DB_PASSWORD)");
                return 0;
            }
            var check = await OkAsync(client.SendAsync(HttpMethod.Post, "api/data-location/check", target, ct), "Can't check that database");
            if (check["ok"]?.GetValue<bool>() != true) throw new CliException($"That database can't be used: {check["problem"]}");
            Confirm($"Move the data there? The project pauses while {check["rowsToCopy"]} rows are copied.");
            var started = DateTimeOffset.UtcNow.AddSeconds(-5);
            await OkAsync(client.SendAsync(HttpMethod.Post, "api/data-location/move", target, ct), "Not moved", HttpStatusCode.Accepted);
            env.Out.WriteLine("Moving (the project's apps are told to try again meanwhile)…");
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), env.Time, ct);
                var (status, now) = await client.SendAsync(HttpMethod.Get, "api/data-location", null, ct);
                if (status == HttpStatusCode.ServiceUnavailable) continue;   // paused while moving
                if (status != HttpStatusCode.OK) throw new CliException(ConsoleClient.ErrorText(now, status));
                if (now["lastMoveFailed"] is JsonObject f && f["at"]!.GetValue<DateTimeOffset>() > started)
                    throw new CliException($"The move failed, and the project carries on where it was: {f["error"]}");
                if (now["previous"] is JsonObject p && p["movedAt"]!.GetValue<DateTimeOffset>() > started)
                {
                    env.Out.WriteLine($"Moved to {now["database"]}. The old copy stays until: winche data:move --finish");
                    return 0;
                }
            }
        }

        // ----- approving another CLI -------------------------------------------------------------------

        /// <summary><c>winche login:approve &lt;code&gt;</c>: allows a CLI waiting with <c>winche login --no-browser</c>, as you.</summary>
        public async Task<int> LoginApproveAsync(string? code, CancellationToken ct)
        {
            code = Ask("The code it shows", code, option: "the code: winche login:approve <code>");
            var client = await ClientAsync(ServerConsole(), ct);
            var (status, found) = await client.SendAsync(HttpMethod.Get, $"api/cli-login/device/{Uri.EscapeDataString(code)}", null, ct);
            if (status == HttpStatusCode.NotFound) throw new CliException("That code isn't right, has expired or was used: check the one it shows.");
            if (status != HttpStatusCode.OK) throw new CliException(ConsoleClient.ErrorText(found, status));
            Confirm($"Allow {found["name"]} ({found["userCode"]}) to use this server as you?");
            await OkAsync(client.SendAsync(HttpMethod.Post, "api/cli-login/approve", new { userCode = found["userCode"]?.ToString(), port = 0 }, ct), "Not allowed");
            env.Out.WriteLine($"Allowed {found["name"]}: it's signed in as you now.");
            return 0;
        }

        // ----- logs ------------------------------------------------------------------------------------

        /// <summary>
        /// <c>winche logs [--product database|storage] [--level warning] [--search …] [--since 1h] [--limit 50] [-f]</c>:
        /// the project's logs, oldest first; <c>--follow</c> keeps printing new ones as they come (Ctrl+C stops).
        /// </summary>
        public async Task<int> LogsAsync(CancellationToken ct)
        {
            var client = await ClientAsync(ct);
            var scope = args.Value("product") switch
            {
                null or "all" => "app",
                "database" or "storage" => args.Value("product"),
                var other => throw new CliException($"--product {other}: database or storage (or all, the default).", 2),
            };
            var filter = "";
            if (args.Value("level") is { } level) filter += $"&level={Uri.EscapeDataString(char.ToUpperInvariant(level[0]) + level[1..].ToLowerInvariant())}";
            if (args.Value("search") is { } q) filter += $"&q={Uri.EscapeDataString(q)}";
            if (args.Value("since") is { } since) filter += $"&from={Uri.EscapeDataString(env.Time.GetUtcNow().Subtract(Duration(since)).ToString("O"))}";
            var limit = args.Value("limit") ?? "50";

            long last = 0;
            var first = await LogPageAsync(client, scope!, filter + $"&limit={limit}", ct);
            foreach (var e in first) { Print(e); last = Math.Max(last, e["id"]!.GetValue<long>()); }
            if (!args.Flag("follow")) { if (first.Count == 0) env.Out.WriteLine("No log entries."); return 0; }

            env.Error.WriteLine("Following new entries (Ctrl+C stops)…");
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), env.Time, ct); }
                catch (OperationCanceledException) { break; }
                foreach (var e in await LogPageAsync(client, scope!, filter + $"&after={last}&limit=500", ct))
                {
                    Print(e);
                    last = Math.Max(last, e["id"]!.GetValue<long>());
                }
            }
            return 0;
        }

        private async Task<List<JsonNode>> LogPageAsync(ConsoleClient client, string scope, string query, CancellationToken ct)
        {
            var (status, body) = await client.SendAsync(HttpMethod.Get, $"api/logs/{scope}?{query.TrimStart('&')}", null, ct);
            if (status == HttpStatusCode.NotFound) throw new CliException($"This project has no {(scope == "app" ? "" : scope + " ")}logs to show.");
            if (status != HttpStatusCode.OK) throw new CliException(ConsoleClient.ErrorText(body, status));
            return [.. (body["entries"]?.AsArray() ?? []).Where(e => e is not null).Select(e => e!).OrderBy(e => e["id"]!.GetValue<long>())];
        }

        private void Print(JsonNode e)
        {
            var at = e["at"]!.GetValue<DateTimeOffset>().ToLocalTime();
            var level = e["level"]?.ToString() switch { "Warning" => "WARN ", "Error" => "ERROR", "Critical" => "CRIT ", "Debug" => "DEBUG", "Trace" => "TRACE", _ => "INFO " };
            env.Out.WriteLine($"{at:HH:mm:ss} {level} {e["message"]}");
            if (e["exception"]?.ToString() is { Length: > 0 } ex) env.Out.WriteLine("    " + ex.Split('\n')[0]);
        }

        private static TimeSpan Duration(string text)
        {
            if (text.Length > 1 && int.TryParse(text[..^1], out var n) && n > 0)
                switch (text[^1]) { case 'm': return TimeSpan.FromMinutes(n); case 'h': return TimeSpan.FromHours(n); case 'd': return TimeSpan.FromDays(n); }
            throw new CliException($"--since {text}: a number and m, h or d (e.g. 30m, 2h, 1d).", 2);
        }
    }
}
