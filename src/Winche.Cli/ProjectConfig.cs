using System.Text.Json;
using System.Text.Json.Serialization;

namespace Winche.Cli;

/// <summary>
/// <c>winche.json</c>, found in the working directory or the nearest parent (like <c>firebase.json</c>):
/// <code>
/// {
///   "console": "https://api.example.com/_console",
///   "projects": { "dev": "http://localhost:5000/_console", "prod": "https://api.example.com/_console" },
///   "rules": { "database": "database.rules", "storage": "storage.rules" },
///   "tests": { "database": "database.rules.test" }
/// }
/// </code>
/// Paths are relative to the file's directory. <c>tests</c> (optional) are <c>winche rules test</c>'s case files.
/// <c>projects</c> (optional) names consoles; <c>winche use &lt;alias&gt;</c> picks one (<see cref="Target"/>).
/// </summary>
internal sealed class ProjectConfig
{
    public const string FileName = "winche.json";

    [JsonPropertyName("console")] public string? Console { get; set; }
    [JsonPropertyName("projects")] public Dictionary<string, string>? Projects { get; set; }
    [JsonPropertyName("rules")] public Dictionary<string, string> Rules { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("tests")] public Dictionary<string, string>? Tests { get; set; }
    /// <summary>The database's indexes and expiry file (winche indexes deploy|diff|pull; default "database.indexes").</summary>
    [JsonPropertyName("indexes")] public string? Indexes { get; set; }
    /// <summary>The project's id, for apps' winche_options.dart (the emulator's /.well-known/winche; default "demo").</summary>
    [JsonPropertyName("projectId")] public string? ProjectId { get; set; }
    /// <summary>The functions folder (winche functions init writes "functions").</summary>
    [JsonPropertyName("functions")] public string? Functions { get; set; }

    [JsonIgnore] public string Directory { get; private set; } = "";
    [JsonIgnore] public string? FilePath { get; private set; }

    public static readonly string[] Subsystems = ["database", "storage"];

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Loads <paramref name="explicitPath"/>, or searches upward from <paramref name="start"/>.</summary>
    public static ProjectConfig? Find(string start, string? explicitPath)
    {
        string? path = null;
        if (explicitPath is not null)
        {
            path = Path.GetFullPath(explicitPath, start);
            if (!File.Exists(path)) throw new CliException($"{explicitPath} not found.");
        }
        else
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, FileName);
                if (File.Exists(candidate)) { path = candidate; break; }
            }
        }
        if (path is null) return null;

        ProjectConfig config;
        try
        {
            config = JsonSerializer.Deserialize<ProjectConfig>(File.ReadAllText(path), Json) ?? new ProjectConfig();
        }
        catch (JsonException ex)
        {
            throw new CliException($"{path}: invalid JSON ({ex.Message}).");
        }
        config.Rules ??= new(StringComparer.Ordinal);
        foreach (var key in config.Rules.Keys.Concat(config.Tests?.Keys ?? Enumerable.Empty<string>()))
            if (!Subsystems.Contains(key))
                throw new CliException($"{path}: unknown key '{key}' under rules/tests (expected database or storage).");
        config.FilePath = path;
        config.Directory = Path.GetDirectoryName(path)!;
        return config;
    }

    public string RulesPath(string subsystem) => Path.GetFullPath(Rules[subsystem], Directory);

    public string? FunctionsPath => Functions is { } f ? Path.GetFullPath(f, Directory) : null;

    [JsonIgnore] public string IndexesPath => Path.GetFullPath(Indexes ?? "database.indexes", Directory);

    public string? TestsPath(string subsystem) =>
        Tests?.GetValueOrDefault(subsystem) is { } file ? Path.GetFullPath(file, Directory) : null;

    public void Save(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json) + Environment.NewLine);
    }
}

/// <summary>Which console a command talks to, and the project alias it came from (if any).</summary>
internal sealed record ConsoleTarget(Uri Url, string? Alias)
{
    public string Describe() => Alias is null ? Url.ToString().TrimEnd('/') : $"{Alias} ({Url.ToString().TrimEnd('/')})";

    /// <summary>
    /// The console: <c>--console</c>, else <c>--project</c>, else <c>WINCHE_CONSOLE</c>, else the project chosen
    /// with <c>winche use</c>, else the only project, else <c>"console"</c> in winche.json.
    /// </summary>
    public static ConsoleTarget? Resolve(ProjectConfig? config, string? consoleArg, string? projectArg, CliEnvironment env)
    {
        if (consoleArg is not null) return new(Parse(consoleArg), null);
        var projects = config?.Projects ?? [];
        if (projectArg is not null)
        {
            if (projects.TryGetValue(projectArg, out var url)) return new(Parse(url), projectArg);
            // Without aliases: a project id on the same Winche Server as the console otherwise used (/p/<id>/…). With
            // aliases, an unknown name is a typo, not a project to go looking for.
            if (projects.Count == 0 && Resolve(config, null, null, env) is { } server && WincheIds.IsValid(projectArg))
                return new(ForProject(server.Url, projectArg), projectArg);
            throw new CliException(projects.Count == 0
                ? $"--project {projectArg}: no console to find it on. Pass --console <url>, or add one: winche use --add {projectArg} <console url>"
                : $"--project {projectArg}: no such project (have {string.Join(", ", projects.Keys)}). A project on the same server: winche use --add {projectArg} <its console> (winche projects lists them).");
        }
        if (env.GetVariable("WINCHE_CONSOLE") is { Length: > 0 } fromEnv) return new(Parse(fromEnv), null);
        if (config?.FilePath is { } file && ActiveProjects.Get(env, file) is { } active)
            return projects.TryGetValue(active, out var url)
                ? new(Parse(url), active)
                : throw new CliException($"The active project '{active}' isn't in {ProjectConfig.FileName} any more. Pick one: winche use <alias>");
        if (projects.Count == 1) return new(Parse(projects.Values.First()), projects.Keys.First());
        if (config?.Console is { } single) return new(Parse(single), null);
        if (projects.Count > 1)
            throw new CliException($"Which project? Run winche use <alias> ({string.Join(", ", projects.Keys)}), or pass --project.");
        return null;
    }

    /// <summary>
    /// The console of project <paramref name="id"/> on the same Winche Server as <paramref name="console"/>:
    /// <c>https://host/_console</c> or <c>https://host/p/other/_console</c> → <c>https://host/p/&lt;id&gt;/_console</c>.
    /// </summary>
    public static Uri ForProject(Uri console, string id)
    {
        var path = console.AbsolutePath.TrimEnd('/');
        var last = path.LastIndexOf('/');
        var (root, prefix) = last < 0 ? ("", path) : (path[..last], path[last..]);
        var marker = root.LastIndexOf("/p/", StringComparison.Ordinal);
        if (marker >= 0 && root.IndexOf('/', marker + 3) < 0) root = root[..marker];
        return new Uri(console, $"{root}/p/{Uri.EscapeDataString(id)}{prefix}/");
    }

    public static Uri Parse(string raw) =>
        Uri.TryCreate(raw.TrimEnd('/') + "/", UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? uri
            : throw new CliException($"'{raw}' isn't a console URL (expected https://host/prefix).");
}

/// <summary>
/// The project chosen with <c>winche use</c> for each checkout (by its winche.json path), kept on this machine in
/// <c>active-projects.json</c> next to the credentials — like the Firebase CLI's active project.
/// </summary>
internal static class ActiveProjects
{
    private static string File(CliEnvironment env) => Path.Combine(env.ConfigDirectory, "active-projects.json");

    private static Dictionary<string, string> Load(CliEnvironment env)
    {
        try
        {
            return System.IO.File.Exists(File(env))
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(File(env))) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string? Get(CliEnvironment env, string configFile) => Load(env).GetValueOrDefault(configFile);

    public static void Set(CliEnvironment env, string configFile, string? alias)
    {
        var all = Load(env);
        if (alias is null) all.Remove(configFile);
        else all[configFile] = alias;
        Directory.CreateDirectory(env.ConfigDirectory);
        System.IO.File.WriteAllText(File(env), JsonSerializer.Serialize(all, ProjectConfig.Json));
    }
}
