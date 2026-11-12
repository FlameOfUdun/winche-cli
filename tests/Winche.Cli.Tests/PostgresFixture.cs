using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Winche.Cli.Tests;

/// <summary>One PostgreSQL container for the run: the app database plus the console's (rules versions).</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("winche_app").Build();

    public string ConnectionString { get; private set; } = null!;
    public string ConsoleConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        ConsoleConnectionString =
            new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "winche_console" }.ConnectionString;
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Clears the rules-version history (tolerates a console that hasn't booted yet).</summary>
    public async Task ResetRulesAsync()
    {
        try
        {
            await using var conn = new NpgsqlConnection(ConsoleConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DO $$
                BEGIN
                    IF to_regclass('console_rule_versions') IS NOT NULL THEN
                        TRUNCATE console_rule_versions;
                    END IF;
                END $$;
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == "3D000")
        {
            // The console database doesn't exist yet.
        }
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
