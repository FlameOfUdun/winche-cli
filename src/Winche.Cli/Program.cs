namespace Winche.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        System.Console.CancelKeyPress += (_, e) =>
        {
            if (cts.IsCancellationRequested) return;   // a second Ctrl+C kills the process
            e.Cancel = true;
            cts.Cancel();
        };
        return await CliApp.RunAsync(args, CliEnvironment.FromProcess(), cts.Token);
    }
}
