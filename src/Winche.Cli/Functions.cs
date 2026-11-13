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

    /// <summary>The files to deploy: code and deno.json, not .winche/, node_modules or hidden folders.</summary>
    public static Dictionary<string, string> Read(string dir)
    {
        if (!Directory.Exists(dir)) throw new CliException($"There's no folder {dir}. Run: winche functions init");
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dir, path).Replace('\\', '/');
            if (rel.Split('/').Any(p => p.StartsWith('.') || p == "node_modules")) continue;
            if (!Kept.Contains(Path.GetExtension(rel))) continue;
            files[rel] = File.ReadAllText(path);
        }
        return files;
    }

    public const string Example = """
        import { callable, documents, HttpsError } from "@winche/functions";

        // Called by apps: functions.call("hello", { name: "Ann" })
        export const hello = callable(async (data: { name?: string }, ctx) => {
          if (!data?.name) throw new HttpsError("invalid-argument", "Send a name.");
          ctx.log("Saying hello to", data.name);
          return { greeting: `Hello ${data.name}`, by: ctx.auth?.uid ?? null };
        });

        // When a document is created: {name} parts of the path are in e.params
        // export const onVisit = documents.onCreated("patients/{patientId}/visits/{visitId}", async (e, ctx) => {
        //   await ctx.db.update(`patients/${e.params.patientId}`, { lastVisit: new Date() });
        // });

        """;

    /// <summary>Makes the folder: an example, deno.json, the SDK's types, VS Code's Deno settings. Keeps what's there.</summary>
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
        Write(".winche/functions.ts", Winche.Functions.FunctionsSdk.Source, always: true);   // the SDK: refreshed with the CLI
        Write(".vscode/settings.json", "{\n  \"deno.enable\": true\n}\n");
        return made;
    }

    /// <summary>Deploys to a server with its deploy key; prints the functions or the problems.</summary>
    public static async Task<int> DeployAsync(HttpClient http, Uri server, string key, string dir, string? note, CliEnvironment env, CancellationToken ct)
    {
        var files = Read(dir);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, "functions/v1/deploy"))
        {
            Content = JsonContent.Create(new { files, note = note ?? $"Deployed from {Path.GetFileName(Path.GetFullPath(dir))}" }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        using var response = await http.SendAsync(request, ct);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct) is { Length: > 0 } t ? t : "{}") as JsonObject ?? [];
        if (!response.IsSuccessStatusCode)
        {
            env.Error.WriteLine($"Not deployed: {body["error"]?["message"]?.GetValue<string>() ?? response.ReasonPhrase}");
            foreach (var p in body["error"]?["details"]?["problems"] as JsonArray ?? []) env.Error.WriteLine($"  {p}");
            return 1;
        }
        var fns = body["functions"] as JsonArray ?? [];
        env.Out.WriteLine($"Deployed version {body["version"]}: {(fns.Count == 0 ? "no functions" : string.Join(", ", fns.Select(f => f?["name"]?.GetValue<string>())))}.");
        return 0;
    }
}
