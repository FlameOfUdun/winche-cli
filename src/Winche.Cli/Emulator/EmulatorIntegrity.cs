using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Winche.Functions;

namespace Winche.Cli.Emulator;

/// <summary>
/// Integrity in the emulator: apps get tokens as on a server (a challenge, then the exchange), but any proof is taken —
/// any app id, any provider, any debug token — since nothing here can check Apple's, Google's or Cloudflare's. Each
/// product is off, monitor (the default: tokens are read, never required) or enforce (requests without one are
/// refused), the same for all of them (<c>--integrity</c>). Functions see <c>ctx.app</c>.
/// </summary>
internal sealed class EmulatorIntegrity(string mode, TextWriter output, bool quiet, TimeProvider? time = null)
{
    public const string Header = "X-Winche-Integrity", QueryParameter = "winche_integrity";
    private static readonly string[] Products = ["/documents/v1", "/files/v1", "/auth/v1", "/functions/v1", "/assets/v1", "/realtime/v1"];
    private readonly ConcurrentDictionary<string, (string App, string Provider, DateTimeOffset Expires)> _tokens = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Mode { get; } = mode;

    /// <summary>The app a request's token says, when it's one this emulator gave and it hasn't expired.</summary>
    public FunctionAppProof? ProofOf(HttpContext http)
    {
        var token = http.Request.Headers[Header].FirstOrDefault() is { Length: > 0 } h ? h.Trim() : http.Request.Query[QueryParameter].FirstOrDefault();
        return token is not null && _tokens.TryGetValue(token, out var t) && t.Expires > _time.GetUtcNow() ? new FunctionAppProof(t.App, t.Provider) : null;
    }

    /// <summary>
    /// What never needs a token (as on a server): email action links, HTTP functions, signed file links, and for reading,
    /// users' photos and the key set; providers' notices at exactly their addresses.
    /// </summary>
    internal static bool Exempt(PathString path, string method)
    {
        if (path.StartsWithSegments("/auth/v1/action") || path.StartsWithSegments("/functions/v1/http") || path.StartsWithSegments("/files/v1/signed")) return true;
        if ((HttpMethods.IsGet(method) || HttpMethods.IsHead(method)) && (path.StartsWithSegments("/auth/v1/.well-known") || path.StartsWithSegments("/auth/v1/photos"))) return true;
        var parts = (path.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        static bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
        return parts.Length == 5 && Is(parts[0], "auth") && Is(parts[1], "v1") && Is(parts[2], "providers")
            && (Is(parts[4], "backchannel-logout") || Is(parts[4], "changed"));
    }

    /// <summary>Whether <paramref name="path"/> is a product's: its path, under it, or its colon verbs (/documents/v1:commit).</summary>
    internal static bool UnderProduct(PathString path, string product)
    {
        var p = path.Value ?? "";
        return p.StartsWith(product, StringComparison.OrdinalIgnoreCase) && (p.Length == product.Length || p[product.Length] is '/' or ':');
    }

    /// <summary>Refuses product requests without an emulator token, when enforcing.</summary>
    public RequestDelegate Gate(RequestDelegate next) => async http =>
    {
        if (Mode == "enforce" && !HttpMethods.IsOptions(http.Request.Method) && !Exempt(http.Request.Path, http.Request.Method)
            && Products.Any(p => UnderProduct(http.Request.Path, p)) && ProofOf(http) is null)
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsJsonAsync(new
            {
                error = new { code = "permission-denied", message = "The emulator is enforcing Integrity (--integrity enforce): the request has no Integrity token.", reason = "integrity-required" },
            });
            return;
        }
        await next(http);
    };

    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/integrity/v1");
        group.MapPost("/challenge", (JsonObject? body) =>
        {
            var challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return Results.Json(new { challenge, expiresAt = _time.GetUtcNow().AddMinutes(5), androidCloudProjectNumber = (long?)null, webSiteKey = (string?)null });
        });
        group.MapPost("/exchange", (JsonObject? body) =>
        {
            var appId = body?["app"]?.GetValue<string>();
            var provider = body?["provider"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(provider))
                return Results.Json(new { error = new { code = "invalid-argument", message = "Send the app's id and how it proves itself (app, provider).", reason = "integrity-proof" } }, statusCode: 400);
            var token = "emu1." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var expires = _time.GetUtcNow().AddHours(1);
            _tokens[token] = (appId, provider, expires);
            if (!quiet) output.WriteLine($"  Integrity: {appId} proved itself ({provider}; the emulator takes any proof)");
            return Results.Json(new { token, expiresAt = expires });
        });
    }
}
