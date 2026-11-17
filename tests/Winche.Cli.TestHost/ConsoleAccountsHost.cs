using Npgsql;

namespace Winche.Cli.TestHost;

/// <summary>The console's accounts, started with the host (as Winche Server does): schema <c>console_accounts</c>.</summary>
internal sealed class ConsoleAccountsHost(string consoleConnection, string masterKey) : Winche.Console.Identity.IConsoleAccountsHost, IHostedService
{
    private WebApplication? _app;

    public IServiceProvider Services => _app?.Services ?? throw new InvalidOperationException("The console accounts haven't started.");

    /// <summary>The console's database and the accounts' schema in it.</summary>
    public static async Task PrepareAsync(string consoleConnection)
    {
        var name = new NpgsqlConnectionStringBuilder(consoleConnection).Database!;
        await using (var server = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(consoleConnection) { Database = "postgres" }.ConnectionString))
        {
            await using var exists = server.CreateCommand("SELECT 1 FROM pg_database WHERE datname = $1");
            exists.Parameters.AddWithValue(name);
            if (await exists.ExecuteScalarAsync() is null)
                try { await using var create = server.CreateCommand($"CREATE DATABASE \"{name}\""); await create.ExecuteNonQueryAsync(); }
                catch (PostgresException e) when (e.SqlState == "42P04") { }   // made meanwhile
        }
        await using var db = NpgsqlDataSource.Create(consoleConnection);
        await using var schema = db.CreateCommand("CREATE SCHEMA IF NOT EXISTS console_accounts");
        await schema.ExecuteNonQueryAsync();
    }

    public async Task StartAsync(CancellationToken ct) =>
        _app = await Winche.Console.Identity.ConsoleAccountsApp.StartAsync(
            new NpgsqlConnectionStringBuilder(consoleConnection) { SearchPath = "console_accounts" }.ConnectionString, masterKey, [],
            "http://localhost/console/accounts", "http://localhost/console/reset-password", ct: ct);

    public async Task StopAsync(CancellationToken ct)
    {
        if (_app is null) return;
        await _app.StopAsync(ct);
        await _app.DisposeAsync();
    }
}
