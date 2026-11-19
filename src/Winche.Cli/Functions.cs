using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Winche.Cli;

/// <summary>Deno for the emulator's functions: the one on PATH, else the pinned release downloaded once.</summary>
internal static class DenoInstaller
{
    public static string? OnPath()
    {
        var name = OperatingSystem.IsWindows() ? "deno.exe" : "deno";
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
    }

    private static string Asset()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "aarch64", Architecture.X64 => "x86_64",
            var a => throw new CliException($"Functions need Deno, and there's no Deno build for {a}: install it yourself (https://deno.com)."),
        };
        if (OperatingSystem.IsMacOS()) return $"deno-{arch}-apple-darwin.zip";
        if (OperatingSystem.IsLinux()) return $"deno-{arch}-unknown-linux-gnu.zip";
        if (OperatingSystem.IsWindows() && arch == "x86_64") return "deno-x86_64-pc-windows-msvc.zip";
        throw new CliException("Functions need Deno: install it (https://deno.com).");
    }

    /// <summary>Deno's path: WINCHE_DENO, PATH, or a download into the CLI's folder (said on the way).</summary>
    public static async Task<string> EnsureAsync(CliEnvironment env, HttpClient http, CancellationToken ct)
    {
        if (env.GetVariable("WINCHE_DENO") is { Length: > 0 } given) return given;
        if (OnPath() is { } found) return found;
        var version = Winche.Functions.FunctionsSdk.DenoVersion;
        var dir = Path.Combine(env.ConfigDirectory, "deno", version);
        var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "deno.exe" : "deno");
        if (File.Exists(exe)) return exe;
        var url = $"https://github.com/denoland/deno/releases/download/v{version}/{Asset()}";
        env.Out.WriteLine($"Downloading Deno {version} for functions (once, into {dir})…");
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, "deno.zip");
        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (!response.IsSuccessStatusCode) throw new CliException($"Couldn't download Deno ({(int)response.StatusCode}). Install it yourself: https://deno.com");
            await using var file = File.Create(zip);
            await response.Content.CopyToAsync(file, ct);
        }
        ZipFile.ExtractToDirectory(zip, dir, overwriteFiles: true);
        File.Delete(zip);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        return exe;
    }
}

/// <summary>A functions folder: its files to deploy, and the starting files for a new one.</summary>
internal static class FunctionsFolder
{
    private static readonly string[] Kept = [".ts", ".tsx", ".js", ".mjs", ".json"];

    /// <summary>The emulator's secrets (NAME=value lines) in the functions folder: never deployed, kept out of git.</summary>
    public const string SecretsFile = ".secrets.local";

    /// <summary>The NAME=value lines of <paramref name="path"/> (# comments and blank lines skipped), or why one is wrong.</summary>
    public static Dictionary<string, string> ReadSecrets(string path, List<string> problems)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return values;
        var n = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            n++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            var name = eq > 0 ? line[..eq].Trim() : line;
            if (eq <= 0 || Winche.Functions.FunctionSecrets.Problem(name) is not null) { problems.Add($"{SecretsFile}:{n}: write NAME=value (NAME in capitals, digits and underscores)."); continue; }
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'')) value = value[1..^1];
            values[name] = value;
        }
        return values;
    }

    /// <summary>The folder (in the functions folder) deployed as ctx.resources: any files, sent by content hash.</summary>
    public const string ResourcesFolder = "resources";

    /// <summary>The resources/ folder's files (path inside it → file on disk), hidden ones skipped.</summary>
    public static SortedDictionary<string, string> ResourceFiles(string dir)
    {
        var resources = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var root = Path.Combine(dir, ResourcesFolder);
        if (!Directory.Exists(root)) return resources;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (rel.Split('/').Any(p => p.StartsWith('.'))) continue;
            resources[rel] = path;
        }
        return resources;
    }

    /// <summary>Each resource's hash and size, with its content (path inside resources/ → entry).</summary>
    public static Dictionary<string, (Winche.Functions.ResourceEntry Entry, string File)> HashResources(string dir)
    {
        var map = new Dictionary<string, (Winche.Functions.ResourceEntry, string)>(StringComparer.Ordinal);
        foreach (var (rel, file) in ResourceFiles(dir))
        {
            using var stream = File.OpenRead(file);
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
            map[rel] = (new Winche.Functions.ResourceEntry(hash, stream.Length), file);
        }
        return map;
    }

    /// <summary>
    /// The files to deploy: code and deno.json, and console pages' web files (HTML, CSS, images, fonts; binary ones as
    /// base64), not resources/ (sent apart, by hash), .winche/, node_modules, hidden files or anything else.
    /// </summary>
    public static Dictionary<string, string> Read(string dir)
    {
        if (!Directory.Exists(dir)) throw new CliException($"There's no folder {dir}. Run: winche functions init");
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dir, path).Replace('\\', '/');
            if (rel.Split('/').Any(p => p.StartsWith('.') || p == "node_modules") || rel.StartsWith(ResourcesFolder + "/", StringComparison.Ordinal)) continue;
            var ext = Path.GetExtension(rel);
            if (Winche.Functions.ConsolePages.Binary.Contains(ext))
                files[rel] = Winche.Functions.ConsolePages.Base64Prefix + Convert.ToBase64String(File.ReadAllBytes(path));
            else if (Kept.Contains(ext) || Winche.Functions.ConsolePages.Extensions.Contains(ext)) files[rel] = File.ReadAllText(path);
        }
        return files;
    }

    public const string Example = """
        import { callable, HttpsError } from "@winche-technologies/winche-functions";
        import { documents } from "@winche-technologies/winche-admin";

        // Called by apps: functions.call("hello", { name: "Ann" })
        export const hello = callable(async (data: { name?: string }, ctx) => {
          if (!data?.name) throw new HttpsError("invalid-argument", "Send a name.");
          ctx.log("Saying hello to", data.name);
          return { greeting: `Hello ${data.name}`, by: ctx.caller?.uid ?? null };
        });

        // When a document is created: {name} parts of the path are in e.params (onDocument from @winche-technologies/winche-functions)
        // export const onVisit = onDocument.created("patients/{patientId}/visits/{visitId}", async (e, ctx) => {
        //   await documents.doc(`patients/${e.params.patientId}`).update({ lastVisit: new Date() });
        // });

        """;

    /// <summary>
    /// Makes the folder: an example, deno.json (the SDKs, @winche-technologies/winche-functions and @winche-technologies/winche-admin, from npm), VS Code's Deno
    /// settings. Keeps what's there.
    /// </summary>
    public static IReadOnlyList<string> Init(string dir)
    {
        var made = new List<string>();
        void Write(string rel, string text, bool always = false)
        {
            var path = Path.Combine(dir, rel);
            if (!always && File.Exists(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            made.Add(rel);
        }
        Write("index.ts", Example);
        Write("deno.json", Winche.Functions.FunctionsSdk.DenoJson + "\n");
        Write(".vscode/settings.json", "{\n  \"deno.enable\": true\n}\n");
        Write(".gitignore", $"{SecretsFile}\n");
        return made;
    }

    /// <summary>Deploys through the console (as the signed-in user or a service account); prints the functions or the problems.</summary>
    /// <param name="plugins">The plugins winche.json lists (the deployment gets exactly these), or null to keep the installed ones.</param>
    public static async Task<int> DeployAsync(ConsoleClient console, string dir, string? note, CliEnvironment env, CancellationToken ct, IReadOnlyList<BuiltPage>? pages = null,
        IReadOnlyList<object>? plugins = null)
    {
        var files = Read(dir);
        PageBuilds.AddTo(files, pages ?? []);
        var resources = HashResources(dir);
        if (resources.Count > 0)
        {
            // Only contents the server doesn't have yet are sent; the rest are named by hash.
            var (asked, answer) = await console.PostAsync("functions/resources/missing", new { hashes = resources.Values.Select(a => a.Entry.Hash).Distinct() }, "functions", ct);
            if (!asked) { env.Error.WriteLine($"Not deployed: {answer["error"]?["message"]?.GetValue<string>() ?? "the console refused the resources."}"); return 1; }
            var missing = (answer["missing"] as JsonArray ?? []).Select(h => h!.GetValue<string>()).ToHashSet();
            var sending = resources.Where(a => missing.Remove(a.Value.Entry.Hash)).ToList();
            if (sending.Count > 0) env.Out.WriteLine($"Sending {sending.Count} new resource{(sending.Count == 1 ? "" : "s")} ({sending.Sum(a => a.Value.Entry.Size) / 1024.0 / 1024:0.#} MB)…");
            foreach (var (rel, (entry, file)) in sending)
            {
                var (sent, refusal) = await console.PutBytesAsync($"functions/resources/{entry.Hash}", await File.ReadAllBytesAsync(file, ct), "functions", ct);
                if (!sent) { env.Error.WriteLine($"Not deployed: resources/{rel}: {refusal["error"]?["message"]?.GetValue<string>() ?? "the console refused it."}"); return 1; }
            }
        }
        var (ok, body) = await console.PostAsync("functions/deploy",
            new { files, note = note ?? $"Deployed from {Path.GetFileName(Path.GetFullPath(dir))}", resources = resources.ToDictionary(a => a.Key, a => a.Value.Entry), plugins }, "functions", ct);
        if (!ok)
        {
            env.Error.WriteLine($"Not deployed: {body["error"]?["message"]?.GetValue<string>() ?? "the console refused it."}");
            foreach (var p in body["error"]?["details"]?["errors"] as JsonArray ?? []) env.Error.WriteLine($"  {p}");
            return 1;
        }
        var fns = body["functions"] as JsonArray ?? [];
        env.Out.WriteLine($"Deployed version {body["version"]}: {(fns.Count == 0 ? "no functions" : string.Join(", ", fns.Select(f => f?["name"]?.GetValue<string>())))}"
            + (resources.Count == 0 ? "." : $", with {resources.Count} resource{(resources.Count == 1 ? "" : "s")}."));
        return 0;
    }
}
