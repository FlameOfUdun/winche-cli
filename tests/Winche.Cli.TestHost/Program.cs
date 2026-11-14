using Winche.Cli.TestHost;
using Winche.Console;
using Winche.Database.DependencyInjection;
using Winche.Storage.DependencyInjection;
using Winche.Storage.Interfaces;

// The console in Keycloak mode with both rules editors, which is what `winche` talks to.
var builder = WebApplication.CreateBuilder(args);
var wincheConn = builder.Configuration["Winche:ConnectionString"]
    ?? throw new InvalidOperationException("Winche:ConnectionString is required.");

// Settings the console saves (the database's indexes and expiry among them).
Winche.Core.ServiceCollectionExtensions.AddWincheConfig(builder.Services, o => { o.ConnectionString = wincheConn; o.MasterKey = "cli-test-host-master-key-0123456789abcdef"; });
builder.Services.AddWincheDatabase(cfg => { cfg.ConnectionString = wincheConn; cfg.UseSettings(); });
builder.Services.AddWincheStorage(opts => opts.ConnectionString = wincheConn);
builder.Services.AddSingleton<IArchive, NoOpArchive>();

builder.Services.AddWincheConsole(o =>
{
    o.ConnectionString = builder.Configuration["Console:ConnectionString"]
        ?? throw new InvalidOperationException("Console:ConnectionString is required.");   // the rules editor's version store
    o.UseKeycloak(k =>
    {
        k.Server = builder.Configuration["Keycloak:Server"];
        k.Realm = builder.Configuration["Keycloak:Realm"];
        k.ClientId = builder.Configuration["Keycloak:Resource"];
    });
    o.AddDatabaseTab(b => b.UseRulesEditor());
    o.AddStorageTab(b => b.UseRulesEditor());
});

var app = builder.Build();
await app.InitializeWincheDatabaseAsync();
await app.InitializeWincheStorageAsync();
app.MapWincheConsole("/_console");
app.Run();

public partial class Program { }
