namespace Winche.Cli;

/// <summary>
/// Addresses on a Winche Server (3.0): its console's API at <c>https://host/console/v1/</c> (signing in, accounts,
/// invites, projects, settings), a project's at <c>https://host/console/v1/projects/&lt;id&gt;/</c>, and the project's
/// apps at <c>https://host/projects/&lt;id&gt;</c>. A command holds one of the first two (a handle); people write the
/// server's address or a project's (<see cref="Parse"/>).
/// </summary>
internal static class ConsoleAddress
{
    private const string Api = "console/v1/";

    /// <summary>The areas that are the server's whichever project a command is about.</summary>
    private static readonly HashSet<string> ServerAreas = new(StringComparer.Ordinal) { "session", "cli", "accounts", "invites", "projects", "server" };

    /// <summary>
    /// What people write as a handle: the server (<c>https://host</c>, also behind a path: <c>https://example.com/winche</c>),
    /// or a project (<c>https://host/projects/&lt;id&gt;</c>, the address its apps use).
    /// </summary>
    public static Uri Parse(string raw)
    {
        if (!Uri.TryCreate(raw.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new CliException($"'{raw}' isn't an address (expected https://host, or a project's https://host/projects/<id>).");
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Contains("_console") || segments.Contains("p"))
            throw new CliException($"'{raw}' is an address of Winche Server before 3.0. Write the server's address (https://host), or a project's: https://host/projects/<id>.");
        var projects = Array.IndexOf(segments, "projects");
        var console = Array.IndexOf(segments, "console");
        var cut = console >= 0 ? console : projects >= 0 ? projects : segments.Length;
        var root = new Uri(uri, "/" + string.Concat(segments[..cut].Select(s => s + "/")));
        var server = new Uri(root, Api);
        // …/projects/<id>, or the console's own …/console/v1/projects/<id> (a handle printed back).
        var id = projects >= 0 && projects + 1 < segments.Length ? Uri.UnescapeDataString(segments[projects + 1]) : null;
        return id is null ? server : ForProject(server, id);
    }

    /// <summary>The server's console API (<c>…/console/v1/</c>) of a handle.</summary>
    public static Uri ServerOf(Uri handle)
    {
        var path = handle.AbsolutePath;
        var at = path.IndexOf("/" + Api, StringComparison.Ordinal);
        return at < 0 ? new Uri(handle, Api) : new Uri(handle, path[..(at + 1)] + Api);
    }

    /// <summary>The project a handle is about, or null (the server's).</summary>
    public static string? ProjectOf(Uri handle)
    {
        var rest = handle.AbsolutePath[ServerOf(handle).AbsolutePath.Length..].Trim('/').Split('/');
        return rest is ["projects", var id, ..] && id.Length > 0 ? Uri.UnescapeDataString(id) : null;
    }

    /// <summary>Project <paramref name="id"/>'s handle on the same server.</summary>
    public static Uri ForProject(Uri handle, string id) => new(ServerOf(handle), $"projects/{Uri.EscapeDataString(id)}/");

    /// <summary>The server's address (<c>https://host</c>), as people write it.</summary>
    public static Uri RootOf(Uri handle) => new(ServerOf(handle), "../../");

    /// <summary>The console's pages (<c>https://host/console/</c>): where people sign in, approve the CLI and work.</summary>
    public static Uri PagesOf(Uri handle) => new(ServerOf(handle), "../");

    /// <summary>A handle as people know it: the server's address, or the project's (its apps' address).</summary>
    public static string Describe(Uri handle) => ProjectOf(handle) is { } id
        ? new Uri(RootOf(handle), $"projects/{Uri.EscapeDataString(id)}").ToString()
        : RootOf(handle).ToString().TrimEnd('/');

    /// <summary>
    /// An API call's address: the server's areas at its console API, the rest at the project's. A project's area asked of
    /// the server's handle (no project picked) says to pick one.
    /// </summary>
    public static Uri Url(Uri handle, string rel)
    {
        var area = rel.Split('/', '?')[0];
        if (ServerAreas.Contains(area)) return new Uri(ServerOf(handle), rel);
        if (ProjectOf(handle) is null)
            throw new CliException($"{Describe(handle)} is a server: pick a project with --project <id> (winche projects lists them), or winche use --add <alias> {Describe(handle)}/projects/<id>.");
        return new Uri(handle, rel);
    }
}
