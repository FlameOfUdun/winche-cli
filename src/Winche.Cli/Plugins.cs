using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Winche.Cli;

internal static partial class CliApp
{
    internal sealed partial class Run
    {
        /// <summary>"name@1.2.0" → (name, 1.2.0); a scoped "@scope/name" keeps its first @.</summary>
        internal static (string Name, string? Version) Spec(string spec)
        {
            var at = spec.LastIndexOf('@');
            return at > 0 ? (spec[..at], spec[(at + 1)..]) : (spec, null);
        }

        /// <summary>--set KEY=value options: values that read as JSON (numbers, true/false) are those; the rest are text.</summary>
        private Dictionary<string, JsonNode?> SetOptions()
        {
            var settings = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var item in args.Values("set"))
            {
                var eq = item.IndexOf('=');
                if (eq <= 0) throw new CliException($"--set {item}: write KEY=value.", 2);
                var raw = item[(eq + 1)..];
                JsonNode? value;
                try { value = raw is "true" or "false" || double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) ? JsonNode.Parse(raw) : JsonValue.Create(raw); }
                catch (JsonException) { value = JsonValue.Create(raw); }
                settings[item[..eq]] = value;
            }
            return settings;
        }

        private void Said(JsonObject body)
        {
            env.Out.WriteLine($"Functions version {body["version"]}: {body["note"]}.");
        }

        private async Task<int> SendPluginAsync(HttpMethod method, string path, object? body, string refusal, CancellationToken ct)
        {
            var client = await ClientAsync(ct);
            var (status, answer) = await client.SendAsync(method, path, body, ct);
            if ((int)status is < 200 or >= 300) throw new CliException($"{refusal}: {ConsoleClient.ErrorText(answer, status)}");
            Said(answer);
            return 0;
        }

        public async Task<int> PluginsListAsync(CancellationToken ct)
        {
            var client = await ClientAsync(ct);
            var list = (await client.GetJsonAsync("functions/plugins", ct))["plugins"] as JsonArray ?? [];
            if (list.Count == 0) { env.Out.WriteLine("No plugins installed. Install one: winche plugins:install <package> --id <id>"); return 0; }
            foreach (var p in list.OfType<JsonObject>())
            {
                var update = p["update"]?.GetValue<bool>() == true ? $" ({p["latest"]} is out: winche plugins:upgrade {p["id"]})" : "";
                env.Out.WriteLine($"{p["id"]}  {p["package"]} {p["version"]}{update}  from the {(p["source"]?.ToString() == "deploy" ? "winche.json" : "console")}");
                env.Out.WriteLine($"  {string.Join(", ", (p["functions"] as JsonArray ?? []).Select(f => f?.ToString()))}");
                foreach (var s in p["secretsNeeded"] as JsonArray ?? [])
                    env.Out.WriteLine($"  needs the secret {s}: winche functions secrets:set {s}");
            }
            return 0;
        }

        public async Task<int> PluginInfoAsync(string spec, CancellationToken ct)
        {
            var (name, version) = Spec(spec);
            var client = await ClientAsync(ct);
            var (status, p) = await client.SendAsync(HttpMethod.Get, $"functions/plugins/package?name={Uri.EscapeDataString(name)}{(version is null ? "" : $"&version={Uri.EscapeDataString(version)}")}", null, ct);
            if (status != HttpStatusCode.OK) throw new CliException(ConsoleClient.ErrorText(p, status));
            var m = p["manifest"]!;
            env.Out.WriteLine($"{p["package"]} {p["version"]}: {m["title"]}");
            if (m["description"] is { } d) env.Out.WriteLine($"  {d}");
            foreach (var a in m["access"] as JsonArray ?? []) env.Out.WriteLine($"  • {a}");
            var settings = (m["settings"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            if (settings.Count > 0) env.Out.WriteLine("Settings (--set KEY=value):");
            foreach (var s in settings)
                env.Out.WriteLine($"  {s["name"]}  {s["label"]} ({s["type"]}{(s["required"]?.GetValue<bool>() == true ? ", needed" : "")}{(s["default"] is { } def ? $", default {def.ToJsonString()}" : "")}{(s["options"] is JsonArray o ? $": {string.Join(" | ", o)}" : "")})");
            var secrets = (m["secrets"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            if (secrets.Count > 0) env.Out.WriteLine("Secrets (each installed copy has its own, ID__NAME):");
            foreach (var s in secrets) env.Out.WriteLine($"  {s["name"]}  {s["label"]}");
            return 0;
        }

        public async Task<int> PluginInstallAsync(string spec, CancellationToken ct)
        {
            var (name, version) = Spec(spec);
            var id = args.Value("id") ?? throw new CliException("Give the id to install it as: --id sms (its functions are then sms.<name>).", 2);
            return await SendPluginAsync(HttpMethod.Post, "functions/plugins", new { id, package = name, version, settings = SetOptions() }, $"Not installed", ct);
        }

        public Task<int> PluginConfigureAsync(string id, CancellationToken ct) =>
            SendPluginAsync(HttpMethod.Put, $"functions/plugins/{Uri.EscapeDataString(id)}", new { settings = SetOptions() }, "Not changed", ct);

        public Task<int> PluginUpgradeAsync(string spec, CancellationToken ct)
        {
            var (id, version) = Spec(spec);
            return SendPluginAsync(HttpMethod.Post, $"functions/plugins/{Uri.EscapeDataString(id)}/upgrade", new { version, settings = SetOptions() }, "Not upgraded", ct);
        }

        public Task<int> PluginUninstallAsync(string id, CancellationToken ct) =>
            SendPluginAsync(HttpMethod.Delete, $"functions/plugins/{Uri.EscapeDataString(id)}{(args.Flag("remove-secrets") ? "?secrets=remove" : "")}", null, "Not removed", ct);

        /// <summary>
        /// The plugins winche.json lists, for a deploy. Plugins installed in the console that the deploy would remove or
        /// change are said first, and asked about (refused without --force when nobody can answer).
        /// </summary>
        public async Task<IReadOnlyList<object>> PluginsForDeployAsync(ConsoleClient client, Dictionary<string, PluginConfig> listed, CancellationToken ct)
        {
            foreach (var (id, p) in listed)
                if (string.IsNullOrWhiteSpace(p.Package)) throw new CliException($"winche.json: plugins.{id} needs a \"package\".", 2);
            var installed = ((await client.GetJsonAsync("functions/plugins?updates=false", ct))["plugins"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(p => p["source"]?.ToString() == "console").ToList();
            var touched = new List<string>();
            foreach (var p in installed)
            {
                var id = p["id"]!.ToString();
                if (!listed.TryGetValue(id, out var want)) { touched.Add($"  - {id} ({p["package"]} {p["version"]}) is removed"); continue; }
                var same = want.Package == p["package"]?.ToString() && (want.Version is null || want.Version == p["version"]?.ToString())
                    && (want.Settings ?? []).All(s => JsonNode.DeepEquals(s.Value, p["settings"]?[s.Key]))
                    && (p["settings"] as JsonObject ?? []).All(s => s.Value is null || want.Settings?.ContainsKey(s.Key) == true);
                if (!same) touched.Add($"  ~ {id} changes to winche.json's ({want.Package} {want.Version ?? "newest"})");
            }
            if (touched.Count > 0 && !args.Flag("force"))
            {
                env.Error.WriteLine("These plugins were installed or changed in the console; deploying winche.json's plugins:");
                foreach (var line in touched) env.Error.WriteLine(line);
                if (!env.Interactive) throw new CliException("Add them to winche.json as they are (winche plugins shows them), or pass --force to deploy winche.json's plugins.");
                env.Out.Write("Deploy anyway? [y/N] ");
                var reply = env.In.ReadLine()?.Trim();
                if (!string.Equals(reply, "y", StringComparison.OrdinalIgnoreCase) && !string.Equals(reply, "yes", StringComparison.OrdinalIgnoreCase))
                    throw new CliException("Nothing deployed.", 1);
            }
            return [.. listed.Select(kv => (object)new { id = kv.Key, package = kv.Value.Package, version = kv.Value.Version, settings = kv.Value.Settings })];
        }
    }
}
