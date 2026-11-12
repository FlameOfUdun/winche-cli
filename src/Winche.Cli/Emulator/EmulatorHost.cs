using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Winche.Auth.AspNetCore;
using Winche.Auth.DependencyInjection;
using Winche.Config;
using Winche.Console;
using Winche.Database.AspNetCore.DependencyInjection;
using Winche.Database.AspNetCore.Rest.DependencyInjection;
using Winche.Database.AspNetCore.WebSockets.DependencyInjection;
using Winche.Database.Constants;
using Winche.Database.DependencyInjection;
using Winche.Rules;
using Winche.Rules.Text;
using Winche.Storage.AspNetCore.DependencyInjection;
using Winche.Storage.AspNetCore.Rest.DependencyInjection;
using Winche.Storage.Constants;
using Winche.Storage.DependencyInjection;
using Winche.Storage.FileSystem;
using Winche.Storage.Interfaces;

namespace Winche.Cli.Emulator;

internal sealed record EmulatorOptions
{
    public int Port { get; init; } = 9090;
    public string Host { get; init; } = "localhost";
    /// <summary>An existing Postgres to use; null starts a temporary one in Docker.</summary>
    public string? ConnectionString { get; init; }
    /// <summary>Where stored files go; null uses a temporary folder.</summary>
    public string? DataDirectory { get; init; }
    public string? DatabaseRules { get; init; }
    public string? StorageRules { get; init; }
    public bool Quiet { get; init; }
    /// <summary>Show the servers' own logs (information and up).</summary>
    public bool Verbose { get; init; }
}

/// <summary>
/// <c>winche emulators</c>: Winche.Database (REST and live WebSocket), Winche.Storage and the console in one local
/// process, on a temporary Postgres, with the rules files applied and reloaded when they change, and sign-in by
/// unsigned test tokens (<see cref="TestTokens"/>).
/// </summary>
internal sealed class EmulatorHost : IAsyncDisposable
{
    public const string ConsoleEmail = "admin@winche.local";
    public const string ConsolePassword = "Winche-emulator1";

    private readonly WebApplication _app;
    private readonly PostgreSqlContainer? _postgres;
    private readonly string? _tempData;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly TextWriter _out;

    public string BaseUrl { get; }
    public string ConnectionString { get; }
    public string DataDirectory { get; }
    public bool TemporaryDatabase => _postgres is not null;
    internal IServiceProvider Services => _app.Services;

    private EmulatorHost(WebApplication app, PostgreSqlContainer? postgres, string? tempData, string baseUrl, string connection, string data, TextWriter output)
    {
        _app = app;
        _postgres = postgres;
        _tempData = tempData;
        BaseUrl = baseUrl;
        ConnectionString = connection;
        DataDirectory = data;
        _out = output;
    }

    public static async Task<EmulatorHost> StartAsync(EmulatorOptions options, TextWriter output, CancellationToken ct)
    {
        var databaseRules = Load(options.DatabaseRules, "database", output);
        var storageRules = Load(options.StorageRules, "storage", output);

        PostgreSqlContainer? postgres = null;
        var connection = options.ConnectionString;
        if (connection is null)
        {
            output.WriteLine("Starting a temporary Postgres in Docker…");
            var pg = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("winche");
            if (!options.Verbose) pg = pg.WithLogger(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            postgres = pg.Build();
            try
            {
                await postgres.StartAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await postgres.DisposeAsync();
                throw new CliException($"Couldn't start Postgres in Docker ({ex.Message.Split('\n')[0]}). Start Docker, or pass --pg <connection string>.");
            }
            connection = postgres.GetConnectionString();
        }

        var tempData = options.DataDirectory is null ? Directory.CreateTempSubdirectory("winche-emulator-").FullName : null;
        var data = options.DataDirectory ?? tempData!;
        Directory.CreateDirectory(data);
        string baseUrl = "";

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(EmulatorHost).Assembly.GetName().Name,   // hosting loads startup code by this name
            EnvironmentName = "Development",
            ContentRootPath = data,   // not the user's folder: its appsettings aren't ours
        });
        // Kestrel can't pick a free port (0) for "localhost": use its IPv4 address then.
        var bind = options.Port == 0 && options.Host == "localhost" ? "127.0.0.1" : options.Host;
        builder.WebHost.UseUrls($"http://{bind}:{options.Port}");
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        builder.Logging.SetMinimumLevel(options.Verbose ? LogLevel.Information : LogLevel.Warning);
        if (!options.Verbose)
        {
            builder.Logging.AddFilter("Microsoft", LogLevel.Error);
            builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None);   // a first start's migration probe "fails" by design
        }

        // Winche Auth: real sign-up and sign-in (/auth/v1), users in the console's Authentication tab. Its tokens are
        // checked; anything else that looks like a JWT is still taken as-is (`winche emulators token`), as before.
        // A random port (0) isn't known until the server starts: links are rewritten to the real address when printed.
        var issuer = options.Port == 0 ? "http://winche-emulator" : $"http://{options.Host}:{options.Port}";
        builder.Services.AddWincheAuth(o =>
        {
            o.ConnectionString = connection;
            o.Issuer = issuer;
            o.UseSettings();   // sign-in methods, lifetimes and email changeable in the console
            o.KeyEncryptionSecret = "winche-emulator-local-only-signing-key-secret";   // local data, a fixed secret
            o.EmailPassword(p => p.MinLength = 6);
            o.Anonymous();
            o.Email(e => e.AppName = "Winche emulator");
            o.Mfa(m => m.Issuer = "Winche emulator");
            // Fake ID tokens from `winche emulators idtoken google|apple <email>` (aud "winche-emulator").
            o.Google(g => { g.Audiences = [TestTokens.IdTokenAudience]; g.AllowUnsignedTokensForTesting = true; });
            o.Apple(a => { a.Audiences = [TestTokens.IdTokenAudience]; a.AllowUnsignedTokensForTesting = true; });   // nothing is sent: links are printed (EmulatorEmailSender)
        });
        builder.Services.AddSingleton<Winche.Auth.Email.IAuthEmailSender>(new EmulatorEmailSender(output, issuer, () => baseUrl));
        // Settings the console can change (trusted providers and token mapping among them), kept in the emulator's database.
        builder.Services.AddWincheConfig(o =>
        {
            o.ConnectionString = connection;
            o.MasterKey = "winche-emulator-local-only-master-key-0123456789";
            o.PollInterval = TimeSpan.FromSeconds(2);
        });
        builder.Services.AddAuthentication().AddWincheTokens(t => t.TrustWincheAuth().UseSettings());
        static IReadOnlyDictionary<string, object?>? Claims(HttpContext http) => WincheClaims.From(http) ?? TestTokens.Claims(http);

        builder.Services.AddWincheDatabase(o =>
        {
            o.ConnectionString = connection;
            o.MapClaims(Claims);
            o.UseRules(databaseRules);
            o.KeepHistory(TimeSpan.FromDays(7));   // the console's History tab
            o.LogRequests(r => r.SuccessSampleRate = 1.0);   // the Logs Explorer: every request, not a sample
        }).AddWincheDatabaseWsApi();
        builder.Services.AddWincheStorage(o =>
        {
            o.ConnectionString = connection;
            o.MapClaims(Claims);
            o.UseRules(storageRules);
            o.LogRequests(r => r.SuccessSampleRate = 1.0);
            // Files in the data folder, through signed URLs on the emulator itself — the same package apps use.
            o.UseFileSystemArchive(fs =>
            {
                fs.Root = Path.Combine(data, "storage");
                fs.SigningKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            });
        });
        builder.Services.AddWincheConsole(o =>
        {
            o.ConnectionString = connection;
            o.SeedAdminEmail = ConsoleEmail;
            o.SeedAdminPassword = ConsolePassword;
            o.AddDatabaseTab(b => { b.UseLogs(); b.UseHistory(); });
            o.AddStorageTab(b => b.UseLogs());
            o.AddAuthenticationTab();
            o.RequireTwoFactorForSettings = false;   // a local, seeded admin: no authenticator to enrol
            o.AddLogsTab(l =>
            {
                l.MinimumLevel = LogLevel.Information;   // locally, see what the servers say…
                l.SetLevel("Microsoft", LogLevel.Warning);   // …but not the framework's chatter (every SQL command)
                l.SetLevel("System", LogLevel.Warning);
                l.FlushInterval = TimeSpan.FromMilliseconds(500);
            });
        });
        builder.Services.AddCors(c => c.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

        var app = builder.Build();
        app.UseCors();
        if (!options.Quiet) app.Use(RequestLog(output));
        app.UseWebSockets();
        app.UseWincheWsQueryToken();
        await app.InitializeWincheDatabaseAsync();
        app.MapWincheDatabaseWsApi();
        app.MapWincheDatabaseRestApi();
        await app.InitializeWincheStorageAsync();
        app.MapWincheStorageRestApi();
        app.MapWincheStorageFileSystem();
        app.MapWincheAuth();
        app.MapWincheConsole("/_console");

        try
        {
            await app.StartAsync(ct);
        }
        catch (IOException ex)
        {
            await app.DisposeAsync();
            if (postgres is not null) await postgres.DisposeAsync();
            throw new CliException($"Can't listen on {options.Host}:{options.Port} ({ex.Message}). Try --port.");
        }

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        baseUrl = address.Replace("127.0.0.1", "localhost").Replace("[::1]", "localhost").TrimEnd('/');

        var host = new EmulatorHost(app, postgres, tempData, baseUrl, connection, data, output);
        host.Watch(options.DatabaseRules, WincheDatabaseKeys.RuleEngine);
        host.Watch(options.StorageRules, WincheStorageKeys.RULE_ENGINE_KEY);
        return host;
    }

    public void PrintBanner(string? databaseRules, string? storageRules)
    {
        var rules = string.Join(", ", new[] { databaseRules, storageRules }.Where(r => r is not null).Select(r => Path.GetFileName(r!)));
        _out.WriteLine();
        _out.WriteLine($"Winche emulator is running{(rules.Length > 0 ? $" — rules from {rules}, reloaded on save" : " — no rules files, so everything is denied")}");
        _out.WriteLine();
        _out.WriteLine($"  Database (REST)   {BaseUrl}/documents");
        _out.WriteLine($"  Database (live)   {BaseUrl.Replace("http", "ws")}/documents/ws");
        _out.WriteLine($"  Storage           {BaseUrl}/files");
        _out.WriteLine($"  Auth              {BaseUrl}/auth/v1   (sign up, sign in, anonymous; or `winche emulators token <uid>`)");
        _out.WriteLine($"  Console           {BaseUrl}/_console   ({ConsoleEmail} / {ConsolePassword})");
        _out.WriteLine($"  Postgres          {(TemporaryDatabase ? "temporary (gone when the emulator stops)" : "the one you passed with --pg")}");
        _out.WriteLine();
        _out.WriteLine("  Sign in as anyone with an unsigned token:");
        _out.WriteLine("    Authorization: Bearer $(winche emulators token u1 role=admin)");
        _out.WriteLine();
    }

    private static RuleSet Load(string? file, string subsystem, TextWriter output)
    {
        if (file is null) return new RuleSet([]);
        try
        {
            return RuleText.ParseRuleSet(File.ReadAllText(file));
        }
        catch (RuleSyntaxException ex)
        {
            throw new CliException($"{Path.GetFileName(file)}:{ex.Line}:{ex.Column}: {ex.Reason}");
        }
        catch (FileNotFoundException)
        {
            output.WriteLine($"! {file} not found: {subsystem} allows nothing until it exists.");
            return new RuleSet([]);
        }
    }

    /// <summary>Re-applies a rules file whenever it's saved; a file with an error leaves the previous rules in force.</summary>
    private void Watch(string? file, string repositoryKey)
    {
        if (file is null) return;
        var repository = _app.Services.GetRequiredKeyedService<IMutableRuleSetRepository>(repositoryKey);
        var name = Path.GetFileName(file);
        var watcher = new FileSystemWatcher(Path.GetDirectoryName(Path.GetFullPath(file))!, name)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        var gate = new object();
        CancellationTokenSource? pending = null;
        void Changed(object? _, FileSystemEventArgs __)
        {
            CancellationTokenSource cts;
            lock (gate)
            {
                pending?.Cancel();
                pending = cts = new CancellationTokenSource();
            }
            _ = Task.Delay(150, cts.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                try
                {
                    repository.Update(RuleText.ParseRuleSet(ReadShared(file)));
                    _out.WriteLine($"{DateTime.Now:HH:mm:ss} ↻ {name} reloaded");
                }
                catch (RuleSyntaxException ex)
                {
                    _out.WriteLine($"{DateTime.Now:HH:mm:ss} ✗ {name}:{ex.Line}:{ex.Column}: {ex.Reason} (the previous rules stay in force)");
                }
                catch (IOException)
                {
                    // Mid-save; the next event retries.
                }
            }, TaskScheduler.Default);
        }
        watcher.Changed += Changed;
        watcher.Created += Changed;
        watcher.Renamed += (s, e) => Changed(s, e);
        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);
    }

    private static string ReadShared(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>One line per database and storage request: method, path (documents decoded), status — denials marked.</summary>
    private static Func<HttpContext, RequestDelegate, Task> RequestLog(TextWriter output) => async (ctx, next) =>
    {
        await next(ctx);
        var path = ctx.Request.Path.Value ?? "";
        if (!(path.StartsWith("/documents", StringComparison.Ordinal) || path.StartsWith("/files", StringComparison.Ordinal))) return;
        var status = ctx.Response.StatusCode;
        var who = TestTokens.Claims(ctx)?.GetValueOrDefault("uid") is { } uid ? $" as {uid}" : " signed out";
        var shown = Readable(path, "/documents/") ?? Readable(path, "/files/") ?? path;
        var note = status == 403 ? "  ← denied by the rules" : "";
        output.WriteLine($"{DateTime.Now:HH:mm:ss} {ctx.Request.Method,-6} {shown}{who} → {status}{note}");
    };

    /// <summary>"/files/cHVibGlj…:upload" → "/files/public/hello.txt:upload": the base64 path the clients send, decoded.</summary>
    private static string? Readable(string path, string prefix)
    {
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var rest = path[prefix.Length..];
        var colon = rest.IndexOf(':');
        var encoded = colon < 0 ? rest : rest[..colon];
        return Decode(encoded) is { } decoded ? prefix + decoded + (colon < 0 ? "" : rest[colon..]) : null;
    }

    private static string? Decode(string b64)
    {
        try
        {
            var s = b64.Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(s));
            return text.Length > 0 && text.All(c => !char.IsControl(c) && c != '\uFFFD') ? text : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Cancelled when the server stops on its own (SIGTERM, for instance), so the CLI stops too.</summary>
    public CancellationToken Stopping =>
        _app.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>().ApplicationStopping;

    public async ValueTask DisposeAsync()
    {
        foreach (var w in _watchers) w.Dispose();
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            try { await _app.StopAsync(stop.Token); } catch (OperationCanceledException) { }
        }
        await _app.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
        if (_tempData is not null)
        {
            try { Directory.Delete(_tempData, recursive: true); } catch (IOException) { }
        }
    }
}

/// <summary>The emulator sends no email: it prints each message's link, to open in a browser.</summary>
internal sealed class EmulatorEmailSender(TextWriter output, string issuer, Func<string> baseUrl) : Winche.Auth.Email.IAuthEmailSender
{
    public Task SendAsync(Winche.Auth.Email.AuthEmailMessage message, CancellationToken ct)
    {
        lock (output)
        {
            output.WriteLine($"{DateTime.Now:HH:mm:ss} ✉ {message.Kind,-14} to {message.To}: \"{message.Subject}\"");
            if (message.Link is not null)
                output.WriteLine($"         {(message.Link.StartsWith(issuer, StringComparison.Ordinal) ? baseUrl() + message.Link[issuer.Length..] : message.Link)}");
        }
        return Task.CompletedTask;
    }
}
