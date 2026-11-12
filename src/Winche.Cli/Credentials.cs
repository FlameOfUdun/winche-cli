using System.Text.Json;
using System.Text.Json.Serialization;

namespace Winche.Cli;

/// <summary>One console's saved sign-in (Keycloak tokens from <c>winche login</c>).</summary>
internal sealed class SavedLogin
{
    [JsonPropertyName("tokenEndpoint")] public required string TokenEndpoint { get; set; }
    [JsonPropertyName("clientId")] public required string ClientId { get; set; }
    [JsonPropertyName("accessToken")] public required string AccessToken { get; set; }
    [JsonPropertyName("refreshToken")] public string? RefreshToken { get; set; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset ExpiresAt { get; set; }
    [JsonPropertyName("user")] public string? User { get; set; }
}

/// <summary>
/// <c>credentials.json</c> in the config directory, keyed by console URL. Readable by the user only
/// (mode 600 on Unix), like the Firebase CLI's token store.
/// </summary>
internal sealed class CredentialStore(string directory)
{
    private string FilePath => Path.Combine(directory, "credentials.json");

    public static string Key(Uri console) => console.ToString().TrimEnd('/');

    public Dictionary<string, SavedLogin> Load()
    {
        if (!File.Exists(FilePath)) return new(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, SavedLogin>>(File.ReadAllText(FilePath), ProjectConfig.Json)
                   ?? new(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new(StringComparer.Ordinal);   // a corrupt store just means "signed out"
        }
    }

    public SavedLogin? Get(Uri console) => Load().GetValueOrDefault(Key(console));

    public void Put(Uri console, SavedLogin? login)
    {
        var all = Load();
        if (login is null) all.Remove(Key(console));
        else all[Key(console)] = login;

        Directory.CreateDirectory(directory);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all, ProjectConfig.Json));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, FilePath, overwrite: true);
    }
}
