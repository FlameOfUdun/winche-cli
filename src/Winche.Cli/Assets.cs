using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Winche.Cli;

/// <summary>An assets folder: its files by path (hidden ones skipped), hashed for publishing.</summary>
internal static class AssetsFolder
{
    public const string DefaultFolder = "assets", DefaultChannel = "production";

    public sealed record Entry(string Path, string File, string Hash, long Size);

    /// <summary>Every file under <paramref name="dir"/> (path inside it → file), sorted, hidden ones and folders skipped.</summary>
    public static List<Entry> Read(string dir)
    {
        if (!Directory.Exists(dir)) throw new CliException($"There's no folder {dir}. Run: winche assets init");
        var list = new List<Entry>();
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            if (rel.Split('/').Any(p => p.StartsWith('.'))) continue;
            using var stream = File.OpenRead(file);
            list.Add(new Entry(rel, file, Convert.ToHexStringLower(SHA256.HashData(stream)), stream.Length));
        }
        if (list.Count == 0) throw new CliException($"{dir} has no files to publish.");
        return [.. list.OrderBy(e => e.Path, StringComparer.Ordinal)];
    }

    /// <summary>The folder against a release's files: added, changed and removed paths.</summary>
    public static (List<string> Added, List<string> Changed, List<string> Removed) Compare(IReadOnlyList<Entry> local, IReadOnlyDictionary<string, string> remote)
    {
        var mine = local.ToDictionary(e => e.Path, e => e.Hash, StringComparer.Ordinal);
        return ([.. mine.Keys.Where(p => !remote.ContainsKey(p))],
            [.. mine.Where(m => remote.TryGetValue(m.Key, out var h) && h != m.Value).Select(m => m.Key)],
            [.. remote.Keys.Where(p => !mine.ContainsKey(p)).Order(StringComparer.Ordinal)]);
    }

    public static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.#} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes} B";

    public const string ExampleTranslation = """
        {
          "hello": "Hello"
        }

        """;
}

internal static partial class CliApp
{
    internal sealed partial class Run
    {
        private string AssetsDir(string? given) => given is not null ? Path.GetFullPath(given, env.WorkingDirectory)
            : Config?.AssetsPath ?? Path.Combine(Config?.Directory ?? env.WorkingDirectory, AssetsFolder.DefaultFolder);

        private string AssetsChannel() => args.Value("channel") ?? Config?.Assets?.Channel ?? AssetsFolder.DefaultChannel;

        private static string Message(JsonObject body, string otherwise) =>
            body["error"]?["message"]?.GetValue<string>() ?? body["error"]?.ToString() ?? otherwise;

        private void Problems(JsonObject body)
        {
            foreach (var p in body["error"]?["details"]?["errors"] as JsonArray ?? []) env.Error.WriteLine($"  {p}");
        }

        /// <summary>The channel's release and its files (path → hash), or null when the channel isn't there yet.</summary>
        private static async Task<(int Release, Dictionary<string, string> Files)?> ChannelFilesAsync(ConsoleClient client, string channel, CancellationToken ct)
        {
            var (ok, page) = await client.GetAsync("api/assets", "assets", ct);
            if (!ok) throw new CliException(Message(page, "The console refused to list the assets."));
            var at = (page["channels"] as JsonArray ?? []).FirstOrDefault(c => c?["name"]?.GetValue<string>() == channel)?["release"]?.GetValue<int>();
            if (at is not { } number) return null;
            var (found, release) = await client.GetAsync($"api/assets/releases/{number}", "assets", ct, missingOk: true);
            if (!found) throw new CliException(Message(release, $"Release {number} couldn't be read."));
            return (number, (release["files"] as JsonArray ?? []).ToDictionary(f => f!["path"]!.GetValue<string>(), f => f!["hash"]!.GetValue<string>(), StringComparer.Ordinal));
        }

        private void PrintChanges((List<string> Added, List<string> Changed, List<string> Removed) d, int limit = 30)
        {
            var lines = d.Added.Select(p => $"  + {p}").Concat(d.Changed.Select(p => $"  ~ {p}")).Concat(d.Removed.Select(p => $"  - {p}")).ToList();
            foreach (var l in lines.Take(limit)) env.Out.WriteLine(l);
            if (lines.Count > limit) env.Out.WriteLine($"  … and {lines.Count - limit} more");
        }

        // ----- assets init ----------------------------------------------------------------------

        public int AssetsInit(string? dir)
        {
            var config = RequireConfig();
            var target = AssetsDir(dir);
            if (!Directory.Exists(target) || !Directory.EnumerateFileSystemEntries(target).Any())
            {
                Directory.CreateDirectory(Path.Combine(target, "translations"));
                File.WriteAllText(Path.Combine(target, "translations", "en.json"), AssetsFolder.ExampleTranslation);
                env.Out.WriteLine($"  created {Path.GetRelativePath(env.WorkingDirectory, Path.Combine(target, "translations", "en.json"))}");
            }
            else env.Out.WriteLine($"  kept    {Path.GetRelativePath(env.WorkingDirectory, target)}/");
            foreach (var (name, content) in new[] { ("assets.rules", Starter("assets")), ("assets.rules.test", StarterTests("assets")) })
            {
                var full = Path.Combine(config.Directory, name);
                if (File.Exists(full)) { env.Out.WriteLine($"  kept    {name}"); continue; }
                File.WriteAllText(full, content);
                env.Out.WriteLine($"  created {name}");
            }
            config.Rules.TryAdd("assets", "assets.rules");
            (config.Tests ??= new(StringComparer.Ordinal)).TryAdd("assets", "assets.rules.test");
            config.Assets ??= new AssetsConfig();
            config.Assets.Folder ??= Path.GetRelativePath(config.Directory, target).Replace('\\', '/');
            config.Assets.Channel ??= AssetsFolder.DefaultChannel;
            config.Save(config.FilePath!);
            env.Out.WriteLine($"  updated {ProjectConfig.FileName}");
            env.Out.WriteLine();
            env.Out.WriteLine("Next: say who may read what in assets.rules (winche rules deploy), then winche assets deploy -m \"First release\".");
            return 0;
        }

        // ----- assets deploy | diff -------------------------------------------------------------

        public async Task<int> AssetsDeployAsync(string? dir, CancellationToken ct)
        {
            var folder = AssetsDir(dir);
            var channel = AssetsChannel();
            var local = AssetsFolder.Read(folder);
            var client = await ClientAsync(ct);
            var current = await ChannelFilesAsync(client, channel, ct);
            var changes = AssetsFolder.Compare(local, current?.Files ?? new Dictionary<string, string>());
            var same = current is not null && changes.Added.Count + changes.Changed.Count + changes.Removed.Count == 0;
            env.Out.WriteLine(current is null
                ? $"{channel} is new: {local.Count} file{(local.Count == 1 ? "" : "s")} ({AssetsFolder.Size(local.Sum(e => e.Size))})."
                : same ? $"The folder is what {channel} has (release #{current.Value.Release})."
                : $"Changes from {channel} (release #{current.Value.Release}):");
            if (!same && current is not null) PrintChanges(changes);
            if (same && !args.Flag("force")) { env.Out.WriteLine("Nothing to publish (--force publishes it again as a new release)."); return 0; }
            if (args.Flag("dry-run")) { env.Out.WriteLine("Dry run: nothing published."); return 0; }

            // Only the contents the server doesn't have are sent; the rest are named by hash.
            var (asked, answer) = await client.PostAsync("api/assets/contents/missing", new { hashes = local.Select(e => e.Hash).Distinct() }, "assets", ct);
            if (!asked) { env.Error.WriteLine($"Not published: {Message(answer, "the console refused the contents.")}"); return 1; }
            var missing = (answer["missing"] as JsonArray ?? []).Select(h => h!.GetValue<string>()).ToHashSet();
            var sending = local.Where(e => missing.Remove(e.Hash)).ToList();
            if (sending.Count > 0)
            {
                env.Out.WriteLine($"Sending {sending.Count} new file{(sending.Count == 1 ? "" : "s")} ({AssetsFolder.Size(sending.Sum(e => e.Size))})…");
                using var slow = new HttpClient(env.Http, disposeHandler: false) { Timeout = TimeSpan.FromHours(2) };
                var uploads = client.Through(slow);
                foreach (var e in sending)
                {
                    var (sent, refusal) = await uploads.PutFileAsync($"api/assets/contents/{e.Hash}", e.File, "assets", ct);
                    if (!sent) { env.Error.WriteLine($"Not published: {e.Path}: {Message(refusal, "the console refused it.")}"); return 1; }
                }
            }
            var note = args.Value("message") ?? $"Published from {Path.GetFileName(folder)}/";
            var (ok, body) = await client.PostAsync("api/assets/releases", new
            {
                note, channel,
                files = local.ToDictionary(e => e.Path, e => new { hash = e.Hash, size = e.Size, mimeType = "" }),
            }, "assets", ct);
            if (!ok) { env.Error.WriteLine($"Not published: {Message(body, "the console refused it.")}"); Problems(body); return 1; }
            env.Out.WriteLine($"Published release #{body["number"]} ({local.Count} file{(local.Count == 1 ? "" : "s")}, {AssetsFolder.Size(local.Sum(e => e.Size))}); {channel} points at it.");
            return 0;
        }

        public async Task<int> AssetsDiffAsync(string? dir, CancellationToken ct)
        {
            var channel = AssetsChannel();
            var local = AssetsFolder.Read(AssetsDir(dir));
            var current = await ChannelFilesAsync(await ClientAsync(ct), channel, ct);
            if (current is null) { env.Out.WriteLine($"There's no channel {channel} yet: deploying makes it, with {local.Count} files."); return 0; }
            var changes = AssetsFolder.Compare(local, current.Value.Files);
            if (changes.Added.Count + changes.Changed.Count + changes.Removed.Count == 0) { env.Out.WriteLine($"No changes from {channel} (release #{current.Value.Release})."); return 0; }
            env.Out.WriteLine($"Changes from {channel} (release #{current.Value.Release}):");
            PrintChanges(changes, int.MaxValue);
            return 0;
        }

        // ----- releases and channels ------------------------------------------------------------

        public async Task<int> AssetsReleasesAsync(CancellationToken ct)
        {
            var client = await ClientAsync(ct);
            var (ok, body) = await client.GetAsync($"api/assets/releases?limit={args.Value("limit") ?? "30"}", "assets", ct);
            if (!ok) throw new CliException(Message(body, "The console refused to list the releases."));
            var (_, page) = await client.GetAsync("api/assets", "assets", ct);
            var channels = (page["channels"] as JsonArray ?? []).GroupBy(c => c!["release"]!.GetValue<int>())
                .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(c => c!["name"]!.GetValue<string>())));
            var releases = body["releases"] as JsonArray ?? [];
            if (releases.Count == 0) { env.Out.WriteLine("No releases yet: winche assets deploy publishes the first."); return 0; }
            foreach (var r in releases)
            {
                var n = r!["number"]!.GetValue<int>();
                var at = DateTimeOffset.Parse(r["at"]!.GetValue<string>()).ToLocalTime();
                env.Out.WriteLine($"#{n,-5} {at:yyyy-MM-dd HH:mm}  {r["files"],5} files {AssetsFolder.Size(r["size"]!.GetValue<long>()),9}  {r["by"]?.GetValue<string>() ?? "",-24} {r["note"]?.GetValue<string>() ?? ""}"
                    + (channels.TryGetValue(n, out var on) ? $"  ← {on}" : ""));
            }
            return 0;
        }

        public async Task<int> AssetsReleaseAsync(string number, CancellationToken ct)
        {
            if (!int.TryParse(number.TrimStart('#'), out var n)) throw new CliException("Usage: winche assets release <number>", 2);
            var (ok, r) = await (await ClientAsync(ct)).GetAsync($"api/assets/releases/{n}", "assets", ct, missingOk: true);
            if (!ok) throw new CliException(Message(r, $"There's no release {n}."));
            env.Out.WriteLine($"Release #{n}: {r["note"]?.GetValue<string>() ?? "no note"} ({r["by"]?.GetValue<string>() ?? "?"}, {DateTimeOffset.Parse(r["at"]!.GetValue<string>()).ToLocalTime():yyyy-MM-dd HH:mm})");
            foreach (var f in r["files"] as JsonArray ?? [])
                env.Out.WriteLine($"  {f!["path"],-50} {AssetsFolder.Size(f["size"]!.GetValue<long>()),9}  {f["mimeType"]}");
            return 0;
        }

        public async Task<int> AssetsChannelsAsync(CancellationToken ct)
        {
            var (ok, page) = await (await ClientAsync(ct)).GetAsync("api/assets", "assets", ct);
            if (!ok) throw new CliException(Message(page, "The console refused to list the channels."));
            var channels = page["channels"] as JsonArray ?? [];
            if (channels.Count == 0) { env.Out.WriteLine("No channels yet: winche assets deploy makes one (--channel, default production)."); return 0; }
            foreach (var c in channels)
                env.Out.WriteLine($"{c!["name"],-16} #{c["release"],-5} {DateTimeOffset.Parse(c["updatedAt"]!.GetValue<string>()).ToLocalTime():yyyy-MM-dd HH:mm}  {c["by"]?.GetValue<string>() ?? ""}");
            return 0;
        }

        public async Task<int> AssetsChannelSetAsync(string? name, string? release, CancellationToken ct)
        {
            if (name is null || release is null || !int.TryParse(release.TrimStart('#'), out var n))
                throw new CliException("Usage: winche assets channels:set <name> <release>", 2);
            var client = await ClientAsync(ct);
            var current = await ChannelFilesAsync(client, name, ct);
            if (current?.Release == n) { env.Out.WriteLine($"{name} already points at #{n}."); return 0; }
            if (current is { } c)
            {
                var (found, target) = await client.GetAsync($"api/assets/releases/{n}", "assets", ct, missingOk: true);
                if (!found) throw new CliException(Message(target, $"There's no release {n}."));
                var files = (target["files"] as JsonArray ?? []).Select(f => new AssetsFolder.Entry(f!["path"]!.GetValue<string>(), "", f["hash"]!.GetValue<string>(), 0)).ToList();
                env.Out.WriteLine($"{name}: #{c.Release} → #{n}");
                PrintChanges(AssetsFolder.Compare(files, c.Files));
                if (n < c.Release && !args.Flag("yes"))
                {
                    if (!env.Interactive) throw new CliException("Pass --yes to roll back without a prompt.", 2);
                    env.Out.Write("Roll back? [y/N] ");
                    var answer = env.In.ReadLine()?.Trim();
                    if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
                    {
                        env.Out.WriteLine("Not moved.");
                        return 1;
                    }
                }
            }
            var (ok, body) = await client.PostAsync($"api/assets/channels/{Uri.EscapeDataString(name)}", new { release = n }, "assets", ct);
            if (!ok) { env.Error.WriteLine($"Not moved: {Message(body, "the console refused it.")}"); return 1; }
            env.Out.WriteLine(current is null ? $"Made {name}, pointing at #{n}." : $"{name} points at #{n} now.");
            return 0;
        }

        public async Task<int> AssetsChannelRemoveAsync(string name, CancellationToken ct)
        {
            var (ok, body) = await (await ClientAsync(ct)).DeleteAsync($"api/assets/channels/{Uri.EscapeDataString(name)}", "assets", ct);
            if (!ok) { env.Error.WriteLine($"Not removed: {Message(body, "the console refused it.")}"); return 1; }
            env.Out.WriteLine($"Removed {name}. Its releases stay; apps following it get nothing until it's made again.");
            return 0;
        }
    }
}
