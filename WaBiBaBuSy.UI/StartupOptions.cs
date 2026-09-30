using System;

namespace WaBiBaBuSy.UI;

/// <summary>Command-line switches: <c>--test-run &lt;scenario.json&gt; [--exit]</c>.</summary>
public sealed class StartupOptions
{
    public string? TestRunPath { get; init; }
    public bool ExitWhenDone { get; init; }

    public static StartupOptions Current { get; private set; } = new();

    public static StartupOptions Parse(string[] args)
    {
        string? path = null;
        bool exit = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--test-run", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) path = args[++i];
            else if (args[i].Equals("--exit", StringComparison.OrdinalIgnoreCase)) exit = true;
        }
        Current = new StartupOptions { TestRunPath = path, ExitWhenDone = exit };
        return Current;
    }
}
