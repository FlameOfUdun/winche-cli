using Winche.Cli.TestHost;
using Winche.Console;
using Winche.Database.DependencyInjection;
using Winche.Storage.DependencyInjection;
using Winche.Storage.Interfaces;

// What `winche` talks to: the console of a Winche app, with its own accounts (a Winche Auth of their own, as on Winche
// Server), both rules editors, indexes and the CLI's tokens.
var builder = WebApplication.CreateBuilder(args);
var wincheConn = builder.Configuration["Winche:ConnectionString"]
    ?? throw new InvalidOperationException("Winche:ConnectionString is required.");
var consoleConn = builder.Configuration["Console:ConnectionString"]
    ?? throw new InvalidOperationException("Console:ConnectionString is required.");
const string masterKey = "cli-test-host-master-key-0123456789abcdef";

// Settings the console saves (the database's indexes and expiry among them), and the CLI's tokens.
Winche.Core.ServiceCollectionExtensions.AddWincheConfig(builder.Services, o => { o.ConnectionString = wincheConn; o.MasterKey = masterKey; });
builder.Services.AddWincheDatabase(cfg => { cfg.ConnectionString = wincheConn; cfg.UseSettings(); });
builder.Services.AddWincheStorage(opts => { opts.ConnectionString = wincheConn; opts.UseSettings(); });
builder.Services.AddSingleton<IArchive, NoOpArchive>();
// Assets, their contents in memory.
Winche.Assets.AssetsServiceCollectionExtensions.AddWincheAssets(builder.Services, o =>
{
    o.ConnectionString = wincheConn;
    o.UseArchive(_ => new MemoryContentArchive());
});

// The console's accounts: their own schema in the console's database.
await ConsoleAccountsHost.PrepareAsync(consoleConn);
var accounts = new ConsoleAccountsHost(consoleConn, masterKey);
builder.Services.AddHostedService(_ => accounts);

builder.Services.AddWincheConsole(o =>
{
    o.ConnectionString = consoleConn;   // the rules editor's version store
    o.Accounts = accounts;
    o.RequireTwoFactorForSettings = false;
    // A new server, as winche setup finds it: no project yet, every product off until started.
    if (builder.Configuration["TestHost:ProjectSetup"] == "true") o.UseProjectSetup(carryOver: false);
    o.AddDatabaseTab(b => b.UseRulesEditor());
    o.AddStorageTab(b => b.UseRulesEditor());
    o.AddAssetsTab(b => b.UseRulesEditor());
});

var app = builder.Build();
await app.InitializeWincheDatabaseAsync();
await app.InitializeWincheStorageAsync();
// As Winche Server lays it out: the console at /console (its API at /console/v1), and this app's project's console API
// at /console/v1/projects/<id> too.
app.MapWincheConsole("/console");
app.MapGroup("/console/v1/projects/{project}").MapWincheConsoleApi();
app.Run();

public partial class Program { }
