using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Winche.Cli;

/// <summary>Signing in as a project's service account: a key or the CI job's own token, exchanged for an access token.</summary>
internal static class ServiceAccounts
{
    /// <summary>Whether <paramref name="token"/> is a key to exchange (a service account's, or a CI token from before them).</summary>
    public static bool IsKey(string token) => token.StartsWith("wsk_", StringComparison.Ordinal) || token.StartsWith("wct_", StringComparison.Ordinal);

    /// <summary>Exchanges <paramref name="body"/> (<c>{ key }</c> or <c>{ assertion }</c>) at the project for an access token.</summary>
    public static async Task<string> ExchangeAsync(HttpClient http, Uri console, object body, CancellationToken ct)
    {
        HttpResponseMessage response;
        try { response = await http.PostAsJsonAsync(ConsoleAddress.Url(console, "service-accounts/token"), body, ct); }
        catch (HttpRequestException ex) { throw new CliException($"Can't reach {ConsoleAddress.Describe(console)} ({ex.Message})."); }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonNode? answer = null;
            try { answer = text.Length > 0 ? JsonNode.Parse(text) : null; } catch (System.Text.Json.JsonException) { }
            if (response.IsSuccessStatusCode && answer?["accessToken"]?.GetValue<string>() is { Length: > 0 } token) return token;
            var why = answer?["error"]?["message"]?.GetValue<string>() ?? $"the server answered {(int)response.StatusCode}";
            throw new CliException($"Couldn't sign in as the service account at {ConsoleAddress.Describe(console)}: {why}");
        }
    }
}
