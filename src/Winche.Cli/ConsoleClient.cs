using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Winche.Cli;

/// <summary>The console's rules API (<c>{console}/rules/...</c>), called with a bearer token.</summary>
internal sealed class ConsoleClient(HttpClient http, Uri console, string token)
{
    internal sealed record VersionSummary(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("isActive")] bool IsActive,
        [property: JsonPropertyName("note")] string? Note,
        [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
        [property: JsonPropertyName("createdBy")] string? CreatedBy,
        [property: JsonPropertyName("revertedFromVersion")] int? RevertedFromVersion = null);

    internal sealed record VersionDetail(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("rulesJson")] string RulesJson);

    private sealed record RulesJsonBody([property: JsonPropertyName("rulesJson")] string RulesJson);

    private sealed record ValidationErrors(
        [property: JsonPropertyName("errors")] List<ValidationError>? Errors);

    private sealed record ValidationError(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("message")] string Message);

    public async Task<IReadOnlyList<VersionSummary>> GetVersionsAsync(string subsystem, CancellationToken ct) =>
        await SendAsync<List<VersionSummary>>(HttpMethod.Get, $"rules/{subsystem}/versions", null, subsystem, ct) ?? [];

    public async Task<VersionDetail?> GetActiveAsync(string subsystem, CancellationToken ct)
    {
        var active = (await GetVersionsAsync(subsystem, ct)).FirstOrDefault(v => v.IsActive);
        return active is null
            ? null
            : await SendAsync<VersionDetail>(HttpMethod.Get, $"rules/{subsystem}/versions/{active.Version}", null, subsystem, ct);
    }

    /// <summary>The rules the server enforces right now (a saved version, or the app's starting rules).</summary>
    public async Task<string> GetLiveAsync(string subsystem, CancellationToken ct) =>
        (await SendAsync<RulesJsonBody>(HttpMethod.Get, $"rules/{subsystem}/live", null, subsystem, ct))!.RulesJson;

    public async Task<VersionDetail> GetVersionAsync(string subsystem, int version, CancellationToken ct) =>
        (await SendAsync<VersionDetail>(HttpMethod.Get, $"rules/{subsystem}/versions/{version}", null, subsystem, ct, $"{subsystem} has no version {version}."))!;

    /// <summary>Makes <paramref name="version"/> active again, as a new version (like the console's Revert).</summary>
    public async Task<VersionDetail> RevertAsync(string subsystem, int version, CancellationToken ct) =>
        (await SendAsync<VersionDetail>(HttpMethod.Post, $"rules/{subsystem}/revert/{version}", null, subsystem, ct, $"{subsystem} has no version {version}."))!;

    public async Task<VersionDetail> SaveAsync(string subsystem, string rulesJson, string note, int? expectedHead, CancellationToken ct) =>
        (await SendAsync<VersionDetail>(HttpMethod.Post, $"rules/{subsystem}",
            new { rulesJson, note, expectedHeadVersion = expectedHead }, subsystem, ct))!;

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, string subsystem, CancellationToken ct, string? notFound = null)
    {
        using var request = new HttpRequestMessage(method, ConsoleAddress.Url(console, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) request.Content = JsonContent.Create(body);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new CliException($"Can't reach {ConsoleAddress.Describe(console)} ({ex.Message}).");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                    throw Refused();
                case HttpStatusCode.Forbidden:
                    throw new CliException("Your account can't manage rules: it needs the console's Admin role.");
                case HttpStatusCode.NotFound when notFound is not null && await HasSubsystemAsync(subsystem, ct):
                    throw new CliException(notFound);
                case HttpStatusCode.NotFound:
                    await NotAProjectAsync(path, ct);
                    throw new CliException(
                        $"The console has no {subsystem} rules editor. Enable it in the host: Add{Title(subsystem)}Tab(b => b.UseRulesEditor()).");
                case HttpStatusCode.Conflict:
                    throw new CliException(
                        $"{subsystem}: the rules changed on the console while deploying. Run the command again.");
                case HttpStatusCode.BadRequest:
                    var errors = TryRead<ValidationErrors>(text)?.Errors;
                    throw new CliException(errors is { Count: > 0 }
                        ? $"{subsystem}: the console rejected the rules:\n" +
                          string.Join("\n", errors.Select(e => $"  {(e.Path is null ? "" : e.Path + ": ")}{e.Message}"))
                        : $"{subsystem}: the console rejected the request: {text}");
            }
            if (!response.IsSuccessStatusCode)
                throw new CliException($"The console answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            if (response.Content.Headers.ContentType?.MediaType != "application/json")
                throw new CliException($"{ConsoleAddress.Describe(console)} doesn't look like a Winche Server 3.0 (got {response.Content.Headers.ContentType?.MediaType ?? "no content type"}).");
            return TryRead<T>(text) ?? throw new CliException("The console sent an unreadable response.");
        }
    }

    /// <summary>
    /// Sends a request and returns its status and JSON answer: only "can't reach it", "not signed in" and "archived"
    /// throw, so commands say what was refused in their own words (<see cref="ErrorText"/>).
    /// </summary>
    public async Task<(HttpStatusCode Status, System.Text.Json.Nodes.JsonObject Body)> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, ConsoleAddress.Url(console, path));
        if (body is not null) request.Content = JsonContent.Create(body);
        if (token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (HttpRequestException ex) { throw new CliException($"Can't reach {ConsoleAddress.Describe(console)} ({ex.Message})."); }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw Refused();
            if (response.StatusCode == HttpStatusCode.Gone) throw new CliException($"{ConsoleAddress.Describe(console)}: this project is archived.");
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) await NotAProjectAsync(path, ct);
            return (response.StatusCode, (text.Length > 0 ? TryNode(text) : null) ?? []);
        }
    }

    /// <summary>GETs a JSON list (<c>accounts</c>, <c>invites</c>): its items, or the refusal.</summary>
    public async Task<System.Text.Json.Nodes.JsonArray> GetListAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ConsoleAddress.Url(console, path));
        if (token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (HttpRequestException ex) { throw new CliException($"Can't reach {ConsoleAddress.Describe(console)} ({ex.Message})."); }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw Refused();
            if (response.StatusCode == HttpStatusCode.NotFound) await NotAProjectAsync(path, ct);
            if (!response.IsSuccessStatusCode) throw new CliException(ErrorText((text.Length > 0 ? TryNode(text) : null) ?? [], response.StatusCode));
            try { return System.Text.Json.Nodes.JsonNode.Parse(text) as System.Text.Json.Nodes.JsonArray ?? []; }
            catch (JsonException) { return []; }
        }
    }

    /// <summary>Nothing more to say on a 404 (a project's area without a project is refused before the call: <see cref="ConsoleAddress.Url"/>).</summary>
    private static Task NotAProjectAsync(string path, CancellationToken ct) => Task.CompletedTask;

    /// <summary>A 401: a service account that may not do this (or was turned off meanwhile), or a sign-in that has ended.</summary>
    private CliException Refused() => token.StartsWith("wsa_", StringComparison.Ordinal)
        ? new CliException("The service account was refused: it only does what its role or scopes allow (and never manages service accounts), and stops once turned off or deleted.")
        : new CliException($"Your sign-in to {ConsoleAddress.Describe(console)} was refused: it has ended (signed out, everywhere too, or the account was turned off). Run: winche login");

    /// <summary>What a refusal says: its message, and its list of problems.</summary>
    public static string ErrorText(System.Text.Json.Nodes.JsonObject body, HttpStatusCode status)
    {
        if (TwoFactor(body)) return "Turn on two-step sign-in for your account first: winche 2fa enable";
        var error = body["error"];
        var message = error is System.Text.Json.Nodes.JsonObject e ? e["message"]?.ToString() : error?.ToString();
        var problems = (error as System.Text.Json.Nodes.JsonObject)?["details"]?["errors"]?.AsArray().Select(x => x?.ToString()).Where(x => x is not null).ToList() ?? [];
        message ??= status switch
        {
            HttpStatusCode.Forbidden => "Your account isn't allowed to do that here (it needs the Admin role).",
            HttpStatusCode.NotFound => "The server doesn't have that here.",
            _ => $"The server answered {(int)status} {status}.",
        };
        return problems.Count == 0 ? message : message + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  - " + p));
    }

    /// <summary>GETs <paramref name="path"/> as JSON; sign-in and role problems throw.</summary>
    public async Task<System.Text.Json.Nodes.JsonObject> GetJsonAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ConsoleAddress.Url(console, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (HttpRequestException ex) { throw new CliException($"Can't reach {ConsoleAddress.Describe(console)} ({ex.Message})."); }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw Refused();
            if (response.StatusCode == HttpStatusCode.Gone) throw new CliException($"{ConsoleAddress.Describe(console)}: this project is archived.");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new CliException("Your account can't see this here: it has no role in this project (or not enough). See your projects: winche projects");
            if (response.StatusCode == HttpStatusCode.NotFound) await NotAProjectAsync(path, ct);
            if (!response.IsSuccessStatusCode) throw new CliException($"The console answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            return System.Text.Json.Nodes.JsonNode.Parse(text) as System.Text.Json.Nodes.JsonObject ?? throw new CliException("The console sent an unreadable response.");
        }
    }

    /// <summary>
    /// POSTs <paramref name="body"/> to <paramref name="path"/>; the answer, and whether it succeeded. Sign-in, role and
    /// reachability problems throw; anything else the console refused comes back for the caller to show.
    /// </summary>
    public Task<(bool Ok, System.Text.Json.Nodes.JsonObject Body)> PostAsync(string path, object body, string what, CancellationToken ct) =>
        SendJsonAsync(HttpMethod.Post, path, body, what, ct);

    /// <summary>PUTs <paramref name="body"/>, like <see cref="PostAsync"/>.</summary>
    public Task<(bool Ok, System.Text.Json.Nodes.JsonObject Body)> PutAsync(string path, object body, string what, CancellationToken ct) =>
        SendJsonAsync(HttpMethod.Put, path, body, what, ct);

    /// <summary>GETs <paramref name="path"/>, like <see cref="PostAsync"/>; with <paramref name="missingOk"/>, a 404 comes back too.</summary>
    public Task<(bool Ok, System.Text.Json.Nodes.JsonObject Body)> GetAsync(string path, string what, CancellationToken ct, bool missingOk = false) =>
        SendContentAsync(HttpMethod.Get, path, null, what, ct, missingOk);

    /// <summary>DELETEs <paramref name="path"/>, like <see cref="PostAsync"/>; a 404 comes back for the caller to word.</summary>
    public Task<(bool Ok, System.Text.Json.Nodes.JsonObject Body)> DeleteAsync(string path, string what, CancellationToken ct) =>
        SendContentAsync(HttpMethod.Delete, path, null, what, ct, missingOk: true);

    /// <summary>PUTs a file's bytes as they're read (large ones too), like <see cref="PostAsync"/>.</summary>
    public async Task<(bool Ok, System.Text.Json.Nodes.JsonObject Body)> PutFileAsync(string path, string file, string what, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        var content = new StreamContent(stream, 1 << 20) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream"), ContentLength = stream.Length } };
        return await SendContentAsync(HttpMethod.Put, path, content, what, ct);
    }

    /// <summary>The same console and sign-in, through <paramref name="other"/> (one with a longer timeout, for uploads).</summary>
    public ConsoleClient Through(HttpClient other) => new(other, console, token);

    /// <summary>PUTs raw bytes (an asset), like <see cref="PostAsync"/>.</summary>
    public Task<(bool Ok, System.Text.Json.Nodes.JsonObject Body)> PutBytesAsync(string path, byte[] content, string what, CancellationToken ct) =>
        SendContentAsync(HttpMethod.Put, path, new ByteArrayContent(content) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }, what, ct);

    private Task<(bool Ok, System.Text.Json.Nodes.JsonObject Body)> SendJsonAsync(HttpMethod method, string path, object body, string what, CancellationToken ct) =>
        SendContentAsync(method, path, JsonContent.Create(body), what, ct);

    private async Task<(bool Ok, System.Text.Json.Nodes.JsonObject Body)> SendContentAsync(HttpMethod method, string path, HttpContent? content, string what, CancellationToken ct, bool missingOk = false)
    {
        using var request = new HttpRequestMessage(method, ConsoleAddress.Url(console, path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (HttpRequestException ex) { throw new CliException($"Can't reach {ConsoleAddress.Describe(console)} ({ex.Message})."); }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            var json = (text.Length > 0 ? TryNode(text) : null) ?? [];
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                    throw Refused();
                case HttpStatusCode.Forbidden when TwoFactor(json):
                    throw new CliException("Turn on two-step sign-in for your console account first (your account menu in the console).");
                case HttpStatusCode.Forbidden:
                    throw new CliException($"Your account can't deploy {what}: it needs the console's Admin role.");
                case HttpStatusCode.NotFound when missingOk && json["error"] is System.Text.Json.Nodes.JsonObject:
                    break;   // the thing asked for isn't there (the console said so): the caller words it
                case HttpStatusCode.NotFound:
                    await NotAProjectAsync(path, ct);
                    throw new CliException($"The console doesn't manage {what} (the app has no Add{Title(what)}…).");
            }
            return (response.IsSuccessStatusCode, json);
        }
    }

    private static bool TwoFactor(System.Text.Json.Nodes.JsonObject json)
    {
        var code = json["error"] is System.Text.Json.Nodes.JsonObject e ? e["code"]?.ToString() : json["error"]?.ToString();
        return code is "TWO_FACTOR_REQUIRED" or "two_factor_setup_required";
    }

    private static System.Text.Json.Nodes.JsonObject? TryNode(string text)
    {
        try { return System.Text.Json.Nodes.JsonNode.Parse(text) as System.Text.Json.Nodes.JsonObject; }
        catch (JsonException) { return null; }
    }

    private async Task<bool> HasSubsystemAsync(string subsystem, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ConsoleAddress.Url(console, $"rules/{subsystem}/versions"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    private static T? TryRead<T>(string text)
    {
        try { return JsonSerializer.Deserialize<T>(text); }
        catch (JsonException) { return default; }
    }

    private static string Title(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
