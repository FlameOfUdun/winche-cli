using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Winche.Cli.Emulator;

/// <summary>
/// The emulator's sign-in: any JWT, signature not checked (Firebase's emulators work the same way). Its claims
/// become the caller's, with <c>uid</c> taken from <c>uid</c>, else <c>sub</c>, else <c>user_id</c>.
/// <c>winche emulators token</c> mints unsigned ones.
/// </summary>
internal static class TestTokens
{
    /// <summary>The audience the emulator's Google and Apple sign-in accept.</summary>
    public const string IdTokenAudience = "winche-emulator";

    /// <summary>
    /// An unsigned Google or Apple ID token for <paramref name="email"/> — the same subject for the same address — that the
    /// emulator's <c>/auth/v1/signIn/idp</c> accepts (and nothing else does).
    /// </summary>
    public static string MintIdToken(string provider, string email)
    {
        var sub = "emu-" + System.Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(email.ToLowerInvariant())))[..20];
        var payload = new Dictionary<string, object?>
        {
            ["iss"] = provider == "apple" ? "https://appleid.apple.com" : "https://accounts.google.com",
            ["aud"] = IdTokenAudience,
            ["sub"] = sub,
            ["email"] = email,
            ["email_verified"] = true,
            ["name"] = email[..email.IndexOf('@')],
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
        };
        return $"{B64("{\"alg\":\"none\",\"typ\":\"JWT\"}")}.{B64(JsonSerializer.Serialize(payload))}.";
    }

    public static string Mint(string uid, IReadOnlyDictionary<string, object?> claims)
    {
        var payload = new Dictionary<string, object?>(claims) { ["uid"] = uid, ["sub"] = uid };
        payload.TryAdd("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return $"{B64("{\"alg\":\"none\",\"typ\":\"JWT\"}")}.{B64(JsonSerializer.Serialize(payload))}.";
    }

    /// <summary>The caller's claims from <c>Authorization: Bearer</c> (or the WebSocket <c>access_token</c>), or null.</summary>
    public static IReadOnlyDictionary<string, object?>? Claims(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim()
            : http.Request.Query["access_token"].FirstOrDefault();
        return token is { Length: > 0 } ? Decode(token) : null;
    }

    public static IReadOnlyDictionary<string, object?>? Decode(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            using var doc = JsonDocument.Parse(WincheBase64Url.DecodeBytes(parts[1]));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var claims = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject()) claims[p.Name] = ToClaim(p.Value);
            if (!claims.ContainsKey("uid") && (claims.GetValueOrDefault("sub") ?? claims.GetValueOrDefault("user_id")) is { } id)
                claims["uid"] = id;
            return claims;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private static object? ToClaim(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : (object)e.GetDouble(),   // (object): else the ternary makes ints doubles
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => e.EnumerateArray().Select(ToClaim).ToList(),
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => ToClaim(p.Value)),
        _ => null,
    };

    private static string B64(string s) => WincheBase64Url.Encode(s);
}
