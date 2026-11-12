namespace Winche.Cli;

/// <summary>A small line diff (LCS) for <c>winche rules diff</c> / <c>deploy --dry-run</c>.</summary>
internal static class LineDiff
{
    /// <summary>Lines prefixed "  ", "- " (only in <paramref name="before"/>) or "+ " (only in <paramref name="after"/>).</summary>
    public static IReadOnlyList<string> Compute(string before, string after)
    {
        var a = Split(before);
        var b = Split(after);
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var result = new List<string>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { result.Add("  " + a[x]); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) result.Add("- " + a[x++]);
            else result.Add("+ " + b[y++]);
        }
        while (x < a.Length) result.Add("- " + a[x++]);
        while (y < b.Length) result.Add("+ " + b[y++]);
        return result;
    }

    private static string[] Split(string text) =>
        text.Length == 0 ? [] : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
}
