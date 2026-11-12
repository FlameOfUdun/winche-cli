using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;

namespace Winche.Cli.Tests;

/// <summary>Mints HS256 bearer tokens for tests, shaped like Keycloak access tokens (realm_access.roles).</summary>
public static class KeycloakTokens
{
    public const string Issuer = "https://kc.test/realms/test";
    public const string Audience = "winche-console";
    public static readonly SymmetricSecurityKey SigningKey =
        new(System.Text.Encoding.UTF8.GetBytes("winche-console-test-signing-key-0123456789"));

    public static string Mint(string[] realmRoles, string sub = "user-1", string email = "user@test", string given = "Test", string family = "User")
    {
        var creds = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256);
        var now = DateTime.UtcNow;
        var payload = new JwtPayload(Issuer, Audience, claims: new[]
        {
            new System.Security.Claims.Claim("sub", sub),
            new System.Security.Claims.Claim("email", email),
            new System.Security.Claims.Claim("given_name", given),
            new System.Security.Claims.Claim("family_name", family),
        }, notBefore: now, expires: now.AddMinutes(10));
        payload["realm_access"] = new Dictionary<string, object> { ["roles"] = realmRoles };

        var token = new JwtSecurityToken(new JwtHeader(creds), payload);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
