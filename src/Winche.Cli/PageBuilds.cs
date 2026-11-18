using System.Diagnostics;

namespace Winche.Cli;

/// <summary>A console page built from its own project: where it is, how it's built, what's deployed.</summary>
internal sealed record BuiltPage(string Folder, string Source, string Build, string Output)
{
    /// <summary>The build's output folder (its files are deployed as <see cref="Folder"/>).</summary>
    public string OutputPath => Path.GetFullPath(Output, Source);
}

/// <summary>
/// Console pages written as a front-end project (<c>"pages"</c> in winche.json): building them, putting their output
/// into the functions deployment, and <c>winche pages init</c>'s template (Vite + React + TypeScript).
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
                throw new CliException($"winche.json: pages.\"{key}\" isn't a folder in the functions (write it as consolePage's files, e.g. \"console/reports\").");
            if (string.IsNullOrWhiteSpace(page.Source))
                throw new CliException($"winche.json: pages.\"{key}\" has no \"source\" (the page's project folder).");
            var source = Path.GetFullPath(page.Source, config.Directory);
            if (!Directory.Exists(source)) throw new CliException($"winche.json: pages.\"{key}\": there's no folder {page.Source}.");
            list.Add(new BuiltPage(folder, source, string.IsNullOrWhiteSpace(page.Build) ? DefaultBuild : page.Build, string.IsNullOrWhiteSpace(page.Output) ? DefaultOutput : page.Output));
        }
        return list;
    }

    /// <summary>Refreshes each page's client (.winche/console-page/) and runs its build; false when one fails.</summary>
    public static async Task<bool> BuildAsync(IReadOnlyList<BuiltPage> pages, CliEnvironment env, CancellationToken ct)
    {
        foreach (var page in pages)
        {
            WriteClient(page.Source);
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

    /// <summary>Writes the page client (<c>@winche/console-page</c>) into a page's project, refreshed with the CLI.</summary>
    public static void WriteClient(string source)
    {
        var dir = Path.Combine(source, ConsolePageClient.Folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.ts"), ConsolePageClient.Index);
        File.WriteAllText(Path.Combine(dir, "react.ts"), ConsolePageClient.React);
    }

    /// <summary>Makes a page project in <paramref name="dir"/> (keeps files already there); the files written.</summary>
    public static IReadOnlyList<string> Init(string dir, string title)
    {
        var made = new List<string>();
        void Write(string rel, string text)
        {
            var path = Path.Combine(dir, rel);
            if (File.Exists(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            made.Add(rel);
        }
        var package = Path.GetFileName(Path.GetFullPath(dir)).ToLowerInvariant().Replace(' ', '-');
        Write("package.json", PackageJson.Replace("{name}", package));
        Write("vite.config.ts", ViteConfig);
        Write("tsconfig.json", TsConfig);
        Write("index.html", IndexHtml.Replace("{title}", System.Net.WebUtility.HtmlEncode(title)));
        Write("src/main.tsx", MainTsx);
        Write("src/App.tsx", AppTsx);
        Write("src/style.css", StyleCss);
        Write(".gitignore", "node_modules\ndist\n.winche\n");
        WriteClient(dir);
        return made;
    }

    private const string PackageJson = """
        {
          "name": "{name}",
          "private": true,
          "type": "module",
          "scripts": {
            "build": "tsc --noEmit && vite build",
            "watch": "vite build --watch"
          },
          "dependencies": {
            "react": "^19.1.0",
            "react-dom": "^19.1.0"
          },
          "devDependencies": {
            "@types/react": "^19.1.6",
            "@types/react-dom": "^19.1.5",
            "@vitejs/plugin-react": "^4.5.0",
            "typescript": "^5.8.3",
            "vite": "^6.3.5"
          }
        }

        """;

    private const string ViteConfig = """
        import { defineConfig } from "vite";
        import react from "@vitejs/plugin-react";
        import { fileURLToPath } from "node:url";

        // A console page is served from a signed address of its own, in a sandboxed frame: its files are addressed
        // relative to the page (base "./"), and it can't fetch (the console runs its actions for it).
        export default defineConfig({
          base: "./",
          plugins: [react()],
          resolve: {
            alias: { "@winche/console-page": fileURLToPath(new URL("./.winche/console-page", import.meta.url)) },
          },
          build: { outDir: "dist", emptyOutDir: true, modulePreload: { polyfill: false } },
        });

        """;

    private const string TsConfig = """
        {
          "compilerOptions": {
            "target": "ES2022",
            "lib": ["ES2022", "DOM", "DOM.Iterable"],
            "module": "ESNext",
            "moduleResolution": "bundler",
            "jsx": "react-jsx",
            "strict": true,
            "noEmit": true,
            "skipLibCheck": true,
            "isolatedModules": true,
            "paths": { "@winche/console-page": ["./.winche/console-page/index.ts"], "@winche/console-page/*": ["./.winche/console-page/*"] }
          },
          "include": ["src", ".winche/console-page"]
        }

        """;

    private const string IndexHtml = """
        <!doctype html>
        <html lang="en">
          <head>
            <meta charset="utf-8" />
            <title>{title}</title>
          </head>
          <body>
            <div id="root"></div>
            <script type="module" src="/src/main.tsx"></script>
          </body>
        </html>

        """;

    private const string MainTsx = """
        import { StrictMode } from "react";
        import { createRoot } from "react-dom/client";
        import { App } from "./App";
        import "./style.css";

        createRoot(document.getElementById("root")!).render(<StrictMode><App /></StrictMode>);

        """;

    private const string AppTsx = """
        import { errorText } from "@winche/console-page";
        import { useAction, useRun, useUser } from "@winche/console-page/react";

        // An example: the action "status" (consoleAction in the functions) shown and kept current while a job runs, and
        // an admin's button that starts it. Replace them with your own actions.
        type Status = { job: { status: "idle" | "running" | "done" | "failed" }; count: number };

        export function App() {
          const user = useUser();
          const status = useAction<Status>("status", null, { refreshEvery: (r) => (r.job.status === "running" ? 3000 : false) });
          const start = useRun("startJob", { confirm: "Start the job?", after: status.refresh });

          if (status.loading) return <p className="muted">Loading…</p>;
          if (status.error) return <p className="error">{errorText(status.error)}</p>;
          return (
            <main>
              <section>
                <h2>Status</h2>
                <p>{status.data!.job.status} · {status.data!.count} items</p>
                {user?.role === "admin" && (
                  <button disabled={start.running || status.data!.job.status === "running"} onClick={() => void start.run()}>
                    Start the job
                  </button>
                )}
              </section>
            </main>
          );
        }

        """;

    private const string StyleCss = """
        /* The console's colors and font arrive as --winche-* variables (light or dark, kept up to date). */
        :root { color-scheme: light dark; }
        :root[data-winche-theme="light"] { color-scheme: light; }
        :root[data-winche-theme="dark"] { color-scheme: dark; }
        body {
          margin: 0;
          padding: 0 2px 24px;
          font: 14px/1.5 var(--winche-font, system-ui, sans-serif);
          color: var(--winche-text, CanvasText);
          background: transparent;
        }
        section {
          background: var(--winche-card, Canvas);
          border: 1px solid var(--winche-border, #ddd);
          border-radius: 8px;
          padding: 16px;
          margin-bottom: 12px;
        }
        h2 { font-size: 15px; margin: 0 0 8px; }
        .muted { color: var(--winche-muted, GrayText); }
        .error { color: #d33; }
        button {
          font: inherit;
          padding: 6px 12px;
          border-radius: 6px;
          border: 1px solid var(--winche-border, #ccc);
          background: var(--winche-accent, #2563eb);
          color: #fff;
          cursor: pointer;
        }
        button:disabled { opacity: 0.5; cursor: default; }

        """;
}
