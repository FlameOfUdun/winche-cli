using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Winche.Cli.Tests;

/// <summary>
/// Real console tokens, got the way <c>winche login</c> gets them: an account signs in to the console, approves the
/// CLI (a PKCE challenge), and the code is traded for the token. The first account is the admin made by setup.
/// </summary>
public static class ConsoleTokens
{
    public const string AdminEmail = "admin@example.com", Password = "Passw0rd!long";

    /// <summary>A CLI sign-in's access token for <paramref name="email"/>, made with <paramref name="role"/> when it's not the admin.</summary>
    public static async Task<string> ForAsync(ConsoleAppFactory app, string email = AdminEmail, string role = "Admin")
    {
        var admin = await SignedInAsync(app, AdminEmail, setup: true);
        if (email != AdminEmail)
        {
            var made = await admin.PostAsJsonAsync("/console/v1/accounts", new { email, role, password = Password });
            if (!made.IsSuccessStatusCode && made.StatusCode != System.Net.HttpStatusCode.Conflict)
                throw new InvalidOperationException($"Couldn't make {email}: {await made.Content.ReadAsStringAsync()}");
        }
        var who = email == AdminEmail ? admin : await SignedInAsync(app, email, setup: false);
        var (challenge, verifier) = Pkce();
        var approved = await who.PostAsJsonAsync("/console/v1/cli/approve", new { challenge, port = 45678, state = "s", name = "tests" });
        var redirect = JsonDocument.Parse(await approved.EnsureSuccessStatusCode().Content.ReadAsStringAsync()).RootElement.GetProperty("redirect").GetString()!;
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(redirect).Query)["code"];
        var token = await app.CreateClient().PostAsJsonAsync("/console/v1/cli/token", new { code, verifier });
        return JsonDocument.Parse(await token.EnsureSuccessStatusCode().Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString()!;
    }

    /// <summary>A browser signed in to the console as <paramref name="email"/> (the first time, setup makes the admin).</summary>
    public static async Task<HttpClient> SignedInAsync(ConsoleAppFactory app, string email, bool setup)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        if (setup) await client.PostAsJsonAsync("/console/v1/session/setup", new { email, password = Password, firstName = "Ada", lastName = "Admin" });   // 409 once made
        var login = await client.PostAsJsonAsync("/console/v1/session/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        return client;
    }

    public static (string Challenge, string Verifier) Pkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return (Base64Url(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier))), verifier);
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
