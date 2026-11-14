using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Winche.Cli;

/// <summary>The console's rules API (<c>{console}/api/rules/...</c>), called with a bearer token.</summary>
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
        await SendAsync<List<VersionSummary>>(HttpMethod.Get, $"api/rules/{subsystem}/versions", null, subsystem, ct) ?? [];

    public async Task<VersionDetail?> GetActiveAsync(string subsystem, CancellationToken ct)
    {
        var active = (await GetVersionsAsync(subsystem, ct)).FirstOrDefault(v => v.IsActive);
        return active is null
            ? null
            : await SendAsync<VersionDetail>(HttpMethod.Get, $"api/rules/{subsystem}/versions/{active.Version}", null, subsystem, ct);
    }

    /// <summary>The rules the server enforces right now (a saved version, or the app's starting rules).</summary>
    public async Task<string> GetLiveAsync(string subsystem, CancellationToken ct) =>
        (await SendAsync<RulesJsonBody>(HttpMethod.Get, $"api/rules/{subsystem}/live", null, subsystem, ct))!.RulesJson;

    public async Task<VersionDetail> GetVersionAsync(string subsystem, int version, CancellationToken ct) =>
        (await SendAsync<VersionDetail>(HttpMethod.Get, $"api/rules/{subsystem}/versions/{version}", null, subsystem, ct, $"{subsystem} has no version {version}."))!;

    /// <summary>Makes <paramref name="version"/> active again, as a new version (like the console's Revert).</summary>
    public async Task<VersionDetail> RevertAsync(string subsystem, int version, CancellationToken ct) =>
        (await SendAsync<VersionDetail>(HttpMethod.Post, $"api/rules/{subsystem}/revert/{version}", null, subsystem, ct, $"{subsystem} has no version {version}."))!;

    public async Task<VersionDetail> SaveAsync(string subsystem, string rulesJson, string note, int? expectedHead, CancellationToken ct) =>
        (await SendAsync<VersionDetail>(HttpMethod.Post, $"api/rules/{subsystem}",
            new { rulesJson, note, expectedHeadVersion = expectedHead }, subsystem, ct))!;

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, string subsystem, CancellationToken ct, string? notFound = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(console, path));
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
            throw new CliException($"Can't reach the console at {console} ({ex.Message}).");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                    throw new CliException($"Not signed in to {console} (or the session ended). Run: winche login");
                case HttpStatusCode.Forbidden:
                    throw new CliException("Your account can't manage rules: it needs the console's Admin role.");
                case HttpStatusCode.NotFound when notFound is not null && await HasSubsystemAsync(subsystem, ct):
                    throw new CliException(notFound);
                case HttpStatusCode.NotFound:
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
                throw new CliException($"{console} doesn't look like a Winche console (got {response.Content.Headers.ContentType?.MediaType ?? "no content type"}). Check the URL ends with the console's prefix, e.g. /_console.");
            return TryRead<T>(text) ?? throw new CliException("The console sent an unreadable response.");
        }
    }

    /// <summary>GETs <paramref name="path"/> as JSON; sign-in and role problems throw.</summary>
    public async Task<System.Text.Json.Nodes.JsonObject> GetJsonAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(console, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (HttpRequestException ex) { throw new CliException($"Can't reach the console at {console} ({ex.Message})."); }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new CliException($"Not signed in to {console} (or the session ended). Run: winche login");
            if (response.StatusCode == HttpStatusCode.Gone) throw new CliException($"{console}: this project is archived.");
            if (!response.IsSuccessStatusCode) throw new CliException($"The console answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            return System.Text.Json.Nodes.JsonNode.Parse(text) as System.Text.Json.Nodes.JsonObject ?? throw new CliException("The console sent an unreadable response.");
        }
    }

    /// <summary>
    /// POSTs <paramref name="body"/> to <paramref name="path"/>; the answer, and whether it succeeded. Sign-in, role and
    /// reachability problems throw; anything else the console refused comes back for the caller to show.
    /// </summary>
    public async Task<(bool Ok, System.Text.Json.Nodes.JsonObject Body)> PostAsync(string path, object body, string what, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(console, path)) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (HttpRequestException ex) { throw new CliException($"Can't reach the console at {console} ({ex.Message})."); }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            var json = (text.Length > 0 ? TryNode(text) : null) ?? [];
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                    throw new CliException($"Not signed in to {console} (or the sign-in or token ended). Run: winche login");
                case HttpStatusCode.Forbidden when TwoFactor(json):
                    throw new CliException("Turn on two-step sign-in for your console account first (your account menu in the console).");
                case HttpStatusCode.Forbidden:
                    throw new CliException($"Your account can't deploy {what}: it needs the console's Admin role.");
                case HttpStatusCode.NotFound:
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
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(console, $"api/rules/{subsystem}/versions"));
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
