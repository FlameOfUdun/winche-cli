namespace Winche.Cli;

internal static class Program
{
    private static Task<int> Main(string[] args) => CliApp.RunAsync(args, CliEnvironment.FromProcess());
}
