using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Winche.Cli.Tests;

/// <summary>Boots the test host: a console with its own accounts, the rules editors, indexes and the CLI's tokens.</summary>
public sealed class ConsoleAppFactory(PostgresFixture fx) : WebApplicationFactory<global::Program>
{
    private string? _admin;

    /// <summary>An admin's console token (as from <c>winche login</c>), made once per app.</summary>
    public async Task<string> AdminTokenAsync() => _admin ??= await ConsoleTokens.ForAsync(this);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Winche:ConnectionString", fx.ConnectionString);
        builder.UseSetting("Console:ConnectionString", fx.ConsoleConnectionString);
    }
}
