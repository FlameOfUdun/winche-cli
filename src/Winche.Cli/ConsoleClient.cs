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
        [property: JsonPropertyName("createdBy")] string? CreatedBy);

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

    public async Task<VersionDetail> SaveAsync(string subsystem, string rulesJson, string note, int? expectedHead, CancellationToken ct) =>
        (await SendAsync<VersionDetail>(HttpMethod.Post, $"api/rules/{subsystem}",
            new { rulesJson, note, expectedHeadVersion = expectedHead }, subsystem, ct))!;

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, string subsystem, CancellationToken ct)
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

    private static T? TryRead<T>(string text)
    {
        try { return JsonSerializer.Deserialize<T>(text); }
        catch (JsonException) { return default; }
    }

    private static string Title(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
