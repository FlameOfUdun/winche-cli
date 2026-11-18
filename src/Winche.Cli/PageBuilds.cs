using System.Diagnostics;

namespace Winche.Cli;

/// <summary>A console page built from its own project: where it is, how it's built, what's deployed.</summary>
internal sealed record BuiltPage(string Folder, string Source, string Build, string Output)
{
    /// <summary>The build's output folder (its files are deployed as <see cref="Folder"/>).</summary>
    public string OutputPath => Path.GetFullPath(Output, Source);
}

/// <summary>
/// Console pages written as a front-end project of their own (<c>"pages"</c> in winche.json): building them, and putting
/// their output into the functions deployment.
/// </summary>
internal static class PageBuilds
{
    public const string DefaultBuild = "npm run build";
    public const string DefaultOutput = "dist";

    /// <summary>winche.json's pages, checked: each folder plain and inside the deployment, each source a folder.</summary>
    public static IReadOnlyList<BuiltPage> Of(ProjectConfig? config)
    {
        if (config?.Pages is not { Count: > 0 } pages) return [];
        var list = new List<BuiltPage>();
        foreach (var (key, page) in pages)
        {
            var folder = string.Join('/', key.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != "."));
            if (folder.Length == 0 || folder.Split('/').Any(p => p is ".." || p.StartsWith('.')) || folder.StartsWith(FunctionsFolder.ResourcesFolder + "/") || folder == FunctionsFolder.ResourcesFolder)
                throw new CliException($"winche.json: pages.\"{key}\" isn't a folder in the functions (write it as pages()'s files, e.g. \"console/reports\").");
            if (string.IsNullOrWhiteSpace(page.Source))
                throw new CliException($"winche.json: pages.\"{key}\" has no \"source\" (the page's project folder).");
            var source = Path.GetFullPath(page.Source, config.Directory);
            if (!Directory.Exists(source)) throw new CliException($"winche.json: pages.\"{key}\": there's no folder {page.Source}.");
            list.Add(new BuiltPage(folder, source, string.IsNullOrWhiteSpace(page.Build) ? DefaultBuild : page.Build, string.IsNullOrWhiteSpace(page.Output) ? DefaultOutput : page.Output));
        }
        return list;
    }

    /// <summary>Runs each page's build; false when one fails.</summary>
    public static async Task<bool> BuildAsync(IReadOnlyList<BuiltPage> pages, CliEnvironment env, CancellationToken ct)
    {
        foreach (var page in pages)
        {
            env.Out.WriteLine($"Building {page.Folder} ({page.Build} in {Path.GetRelativePath(env.WorkingDirectory, page.Source)})…");
            var shell = OperatingSystem.IsWindows() ? ("cmd.exe", "/c") : ("/bin/sh", "-c");
            var start = new ProcessStartInfo(shell.Item1) { WorkingDirectory = page.Source, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(shell.Item2);
            start.ArgumentList.Add(page.Build);
            using var process = Process.Start(start) ?? throw new CliException($"Couldn't run {page.Build}.");
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) env.Out.WriteLine("  " + e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) env.Error.WriteLine("  " + e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try { await process.WaitForExitAsync(ct); }
            catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch { /* gone */ } throw; }
            if (process.ExitCode != 0)
            {
                env.Error.WriteLine($"{page.Folder}: the build failed ({page.Build} exited with {process.ExitCode}); nothing was deployed.");
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Puts each page's build output into the deployment's files as its folder (replacing anything there): the web
    /// files a page may have, binary ones as base64.
    /// </summary>
    public static void AddTo(Dictionary<string, string> files, IReadOnlyList<BuiltPage> pages)
    {
        foreach (var page in pages)
        {
            var output = page.OutputPath;
            if (!File.Exists(Path.Combine(output, "index.html")))
                throw new CliException($"{page.Folder}: there's no index.html in {output} (build it: {page.Build}, in {page.Source}).");
            foreach (var key in files.Keys.Where(k => k.StartsWith(page.Folder + "/", StringComparison.Ordinal)).ToList()) files.Remove(key);
            foreach (var path in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(output, path).Replace('\\', '/');
                if (rel.Split('/').Any(p => p.StartsWith('.'))) continue;
                var ext = Path.GetExtension(rel);
                var name = $"{page.Folder}/{rel}";
                if (Winche.Functions.ConsolePages.Binary.Contains(ext))
                    files[name] = Winche.Functions.ConsolePages.Base64Prefix + Convert.ToBase64String(File.ReadAllBytes(path));
                else if (Winche.Functions.ConsolePages.Extensions.Contains(ext)) files[name] = File.ReadAllText(path);
            }
        }
    }
}
