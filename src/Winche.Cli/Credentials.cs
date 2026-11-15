using System.Text.Json;
using System.Text.Json.Serialization;

namespace Winche.Cli;

/// <summary>One account's sign-in on one server, from <c>winche login</c>: a session of the account (renewed).</summary>
internal sealed class SavedLogin
{
    [JsonPropertyName("user")] public string User { get; set; } = "";
    [JsonPropertyName("accessToken")] public required string AccessToken { get; set; }
    [JsonPropertyName("refreshToken")] public required string RefreshToken { get; set; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>A server's sign-ins: every account signed in there, and the one commands use.</summary>
internal sealed class ServerLogins
{
    [JsonPropertyName("active")] public string? Active { get; set; }
    [JsonPropertyName("accounts")] public Dictionary<string, SavedLogin> Accounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// <c>credentials.json</c> in the config directory: sign-ins by server (one for all its projects, as accounts are the
/// server's), each with its accounts and the active one. Readable by the user only (mode 600 on Unix), like the
/// Firebase CLI's.
/// </summary>
internal sealed class CredentialStore(string directory)
{
    private string FilePath => Path.Combine(directory, "credentials.json");

    private sealed class File_
    {
        [JsonPropertyName("servers")] public Dictionary<string, ServerLogins> Servers { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// The server's console for a project's (<c>…/p/wards/_console/</c> → <c>…/_console/</c>): where the CLI signs in, once
    /// for every project there.
    /// </summary>
    public static Uri ServerOf(Uri console)
    {
        var segments = console.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var path = segments.Length >= 3 && segments[0] == "p" ? string.Join('/', segments[2..]) : string.Join('/', segments);
        return new Uri(console, "/" + path + "/");
    }

    /// <summary>How a server is named in the store and in messages: its console's address.</summary>
    public static string Key(Uri console) => ServerOf(console).ToString().TrimEnd('/');

    private File_ Load()
    {
        if (!File.Exists(FilePath)) return new();
        try { return JsonSerializer.Deserialize<File_>(File.ReadAllText(FilePath), ProjectConfig.Json) ?? new(); }
        catch (JsonException) { return new(); }   // a corrupt (or older) store just means "signed out"
    }

    private void Save(File_ all)
    {
        Directory.CreateDirectory(directory);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all, ProjectConfig.Json));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, FilePath, overwrite: true);
    }

    public ServerLogins? Server(Uri console) => Load().Servers.GetValueOrDefault(Key(console));

    public IReadOnlyDictionary<string, ServerLogins> All() => Load().Servers;

    /// <summary>The account commands use on <paramref name="console"/>'s server, or null.</summary>
    public SavedLogin? Active(Uri console) =>
        Server(console) is { Active: { } active } s ? s.Accounts.GetValueOrDefault(active) : null;

    /// <summary>Saves <paramref name="login"/> for its account (replacing an earlier one); <paramref name="use"/>: and makes it the active one.</summary>
    public void Put(Uri console, SavedLogin login, bool use = true)
    {
        var all = Load();
        var server = all.Servers.TryGetValue(Key(console), out var s) ? s : all.Servers[Key(console)] = new ServerLogins();
        server.Accounts[login.User] = login;
        if (use || server.Active is null) server.Active = login.User;
        Save(all);
    }

    /// <summary>Makes <paramref name="user"/> the active account; false when it isn't signed in there.</summary>
    public bool Use(Uri console, string user)
    {
        var all = Load();
        if (!all.Servers.TryGetValue(Key(console), out var s) || !s.Accounts.ContainsKey(user)) return false;
        s.Active = s.Accounts.Keys.First(k => string.Equals(k, user, StringComparison.OrdinalIgnoreCase));
        Save(all);
        return true;
    }

    /// <summary>Forgets <paramref name="user"/>'s sign-in there; the next one becomes active.</summary>
    public void Remove(Uri console, string user)
    {
        var all = Load();
        if (!all.Servers.TryGetValue(Key(console), out var s)) return;
        s.Accounts.Remove(user);
        if (string.Equals(s.Active, user, StringComparison.OrdinalIgnoreCase)) s.Active = s.Accounts.Keys.FirstOrDefault();
        if (s.Accounts.Count == 0) all.Servers.Remove(Key(console));
        Save(all);
    }
}
