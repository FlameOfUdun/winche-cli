using System.Text.Json;
using System.Text.Json.Serialization;

namespace Winche.Cli;

/// <summary>
/// <c>winche.json</c>, found in the working directory or the nearest parent:
/// <code>
/// {
///   "server": "https://api.example.com",
///   "project": "clinic",
///   "projects": { "dev": "http://localhost:5000/projects/clinic", "prod": "https://api.example.com/projects/clinic" },
///   "rules": { "database": "database.rules", "storage": "storage.rules", "assets": "assets.rules" },
///   "assets": { "folder": "assets", "channel": "production" },
///   "tests": { "database": "database.rules.test" }
/// }
/// </code>
/// Paths are relative to the file's directory. <c>tests</c> (optional) are <c>winche rules test</c>'s case files.
/// <c>server</c> and <c>project</c>: the Winche Server and the project commands work on. <c>projects</c> (optional)
/// names projects by their address (<c>https://host/projects/&lt;id&gt;</c>); <c>winche use &lt;alias&gt;</c> picks one.
/// </summary>
internal sealed class ProjectConfig
{
    public const string FileName = "winche.json";

    /// <summary>Before Winche Server 3.0: a console's address. Refused, with how to write <see cref="Server"/> and <see cref="Project"/>.</summary>
    [JsonPropertyName("console")] public string? Console { get; set; }
    /// <summary>The Winche Server (<c>https://host</c>).</summary>
    [JsonPropertyName("server")] public string? Server { get; set; }
    /// <summary>The project's id on <see cref="Server"/>.</summary>
    [JsonPropertyName("project")] public string? Project { get; set; }
    [JsonPropertyName("projects")] public Dictionary<string, string>? Projects { get; set; }
    [JsonPropertyName("rules")] public Dictionary<string, string> Rules { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("tests")] public Dictionary<string, string>? Tests { get; set; }
    /// <summary>The database's indexes and expiry file (winche indexes deploy|diff|pull; default "database.indexes").</summary>
    [JsonPropertyName("indexes")] public string? Indexes { get; set; }
    /// <summary>The project's id, for apps' winche_options.dart (the emulator's /.well-known/winche; default "demo").</summary>
    [JsonPropertyName("projectId")] public string? ProjectId { get; set; }
    /// <summary>The functions folder (winche functions init writes "functions").</summary>
    [JsonPropertyName("functions")] public string? Functions { get; set; }
    /// <summary>The assets folder and the channel it's published to (winche assets deploy; the emulator serves it).</summary>
    [JsonPropertyName("assets")] public AssetsConfig? Assets { get; set; }
    /// <summary>
    /// Console pages built from a project of their own (React, Vite, …): the folder in the functions deployment that a
    /// pages()'s <c>files</c> names → where its project is and how it's built.
    /// </summary>
    [JsonPropertyName("pages")] public Dictionary<string, PageConfig>? Pages { get; set; }

    [JsonIgnore] public string Directory { get; private set; } = "";
    [JsonIgnore] public string? FilePath { get; private set; }

    public static readonly string[] Subsystems = ["database", "storage", "assets"];

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
                throw new CliException($"{path}: unknown key '{key}' under rules/tests (expected database, storage or assets).");
        config.FilePath = path;
        config.Directory = Path.GetDirectoryName(path)!;
        return config;
    }

    public string RulesPath(string subsystem) => Path.GetFullPath(Rules[subsystem], Directory);

    public string? FunctionsPath => Functions is { } f ? Path.GetFullPath(f, Directory) : null;

    /// <summary>The assets folder, when winche.json names one.</summary>
    [JsonIgnore] public string? AssetsPath => Assets?.Folder is { } f ? Path.GetFullPath(f, Directory) : null;

    [JsonIgnore] public string AssetsCdnPath => Path.GetFullPath(Assets?.Cdn ?? "assets.cdn.json", Directory);

    [JsonIgnore] public string IndexesPath => Path.GetFullPath(Indexes ?? "database.indexes", Directory);

    public string? TestsPath(string subsystem) =>
        Tests?.GetValueOrDefault(subsystem) is { } file ? Path.GetFullPath(file, Directory) : null;

    public void Save(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json) + Environment.NewLine);
    }
}

/// <summary>
/// A console page with a build: <c>"console/reports": { "source": "pages/reports" }</c> builds <c>pages/reports</c> with
/// <c>npm run build</c> and deploys its <c>dist</c> folder as <c>console/reports</c> in the functions.
/// </summary>
internal sealed class PageConfig
{
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("build")] public string? Build { get; set; }
    [JsonPropertyName("output")] public string? Output { get; set; }
}

/// <summary><c>"assets"</c> in winche.json: the folder published as a release, and the channel moved to it.</summary>
internal sealed class AssetsConfig
{
    [JsonPropertyName("folder")] public string? Folder { get; set; }
    [JsonPropertyName("channel")] public string? Channel { get; set; }
    /// <summary>The files kept on a CDN (path → address), published by address: default assets.cdn.json beside winche.json.</summary>
    [JsonPropertyName("cdn")] public string? Cdn { get; set; }
}

/// <summary>Which console a command talks to, and the project alias it came from (if any).</summary>
internal sealed record ConsoleTarget(Uri Url, string? Alias)
{
    public string Describe() => Alias is null ? ConsoleAddress.Describe(Url) : $"{Alias} ({ConsoleAddress.Describe(Url)})";

    /// <summary>
    /// The project (or server): <c>--console</c>, else <c>--project</c>, else <c>WINCHE_CONSOLE</c>, else the project
    /// chosen with <c>winche use</c>, else the only alias, else <c>"server"</c> and <c>"project"</c> in winche.json.
    /// </summary>
    public static ConsoleTarget? Resolve(ProjectConfig? config, string? consoleArg, string? projectArg, CliEnvironment env)
    {
        if (config?.Console is { } old)
            throw new CliException($"{ProjectConfig.FileName}: \"console\" ({old}) is how Winche Server named a project before 3.0. Write \"server\": \"https://host\" and \"project\": \"<id>\" instead.");
        if (config?.Projects?.Values.FirstOrDefault(v => v.Contains("_console", StringComparison.Ordinal) || v.Contains("/p/", StringComparison.Ordinal)) is { } oldAlias)
            throw new CliException($"{ProjectConfig.FileName}: \"projects\" names {oldAlias}, an address before Winche Server 3.0. Write each as the project's address: https://host/projects/<id>.");
        // --console with --project <id>: that project on the same server (an id, not an alias of winche.json's).
        if (consoleArg is not null)
            return projectArg is not null && WincheIds.IsValid(projectArg) && config?.Projects?.ContainsKey(projectArg) != true
                ? new(ForProject(Parse(consoleArg), projectArg), projectArg)
                : new(Parse(consoleArg), null);
        var projects = config?.Projects ?? [];
        if (projectArg is not null)
        {
            if (projects.TryGetValue(projectArg, out var url)) return new(Parse(url), projectArg);
            // Without aliases: a project id on the same Winche Server as the one otherwise used. With
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
        if (config?.Server is { } configured)
            return new(config.Project is { Length: > 0 } project ? ForProject(Parse(configured), project) : Parse(configured), null);
        if (projects.Count > 1)
            throw new CliException($"Which project? Run winche use <alias> ({string.Join(", ", projects.Keys)}), or pass --project.");
        return null;
    }

    /// <summary>Project <paramref name="id"/> on the same Winche Server as <paramref name="console"/>.</summary>
    public static Uri ForProject(Uri console, string id) => ConsoleAddress.ForProject(console, id);

    /// <summary>The server's address (<c>https://host</c>) or a project's (<c>https://host/projects/&lt;id&gt;</c>).</summary>
    public static Uri Parse(string raw) => ConsoleAddress.Parse(raw);
}

/// <summary>
/// The project chosen with <c>winche use</c> for each checkout (by its winche.json path), kept on this machine in
/// <c>active-projects.json</c> next to the credentials.
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
