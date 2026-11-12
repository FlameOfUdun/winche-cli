namespace Winche.Cli;

/// <summary>Everything the CLI touches outside itself, so tests can run it in-process.</summary>
internal sealed class CliEnvironment
{
    public required TextWriter Out { get; init; }
    public required TextWriter Error { get; init; }
    public required string WorkingDirectory { get; init; }

    /// <summary>Directory holding <c>credentials.json</c> (default <c>~/.config/winche</c>).</summary>
    public required string ConfigDirectory { get; init; }

    public required Func<string, string?> GetVariable { get; init; }

    /// <summary>Sends every HTTP request; tests swap in a test server's handler.</summary>
    public required HttpMessageHandler Http { get; init; }

    /// <summary>Opens a URL in the user's browser (login).</summary>
    public required Action<string> OpenBrowser { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public static CliEnvironment FromProcess()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new CliEnvironment
        {
            Out = Console.Out,
            Error = Console.Error,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            ConfigDirectory = Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg, "winche"),
            GetVariable = Environment.GetEnvironmentVariable,
            Http = new SocketsHttpHandler(),
            OpenBrowser = Browser.Open,
        };
    }
}

internal static class Browser
{
    public static void Open(string url)
    {
        try
        {
            var (file, args) = OperatingSystem.IsWindows() ? ("cmd", $"/c start \"\" \"{url.Replace("&", "^&")}\"")
                : OperatingSystem.IsMacOS() ? ("open", $"\"{url}\"")
                : ("xdg-open", $"\"{url}\"");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, args) { UseShellExecute = false })?.Dispose();
        }
        catch
        {
            // The URL is printed too; the user can open it by hand.
        }
    }
}

/// <summary>A failure to report to the user as one line, with an exit code.</summary>
internal sealed class CliException(string message, int exitCode = 1) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
