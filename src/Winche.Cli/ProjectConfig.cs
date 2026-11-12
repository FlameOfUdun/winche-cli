using System.Text.Json;
using System.Text.Json.Serialization;

namespace Winche.Cli;

/// <summary>
/// <c>winche.json</c>, found in the working directory or the nearest parent (like <c>firebase.json</c>):
/// <code>
/// {
///   "console": "https://api.example.com/_console",
///   "rules": { "database": "database.rules", "storage": "storage.rules" }
/// }
/// </code>
/// Rule file paths are relative to the file's directory.
/// </summary>
internal sealed class ProjectConfig
{
    public const string FileName = "winche.json";

    [JsonPropertyName("console")] public string? Console { get; set; }
    [JsonPropertyName("rules")] public Dictionary<string, string> Rules { get; set; } = new(StringComparer.Ordinal);

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
        foreach (var key in config.Rules.Keys)
            if (!Subsystems.Contains(key))
                throw new CliException($"{path}: unknown rules key '{key}' (expected database or storage).");
        config.FilePath = path;
        config.Directory = Path.GetDirectoryName(path)!;
        return config;
    }

    public string RulesPath(string subsystem) => Path.GetFullPath(Rules[subsystem], Directory);

    public void Save(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json) + Environment.NewLine);
    }
}
