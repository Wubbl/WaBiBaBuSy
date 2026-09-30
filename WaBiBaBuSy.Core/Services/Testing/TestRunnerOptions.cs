using WaBiBaBuSy.Models.Configuration;

namespace WaBiBaBuSy.Core.Services.Testing;

public sealed class TestRunnerOptions
{
    /// <summary>A probe instant is never closer than this to "now" (command must reach every node first).</summary>
    public int MinLeadMs { get; set; } = 500;
    /// <summary>A node that has not answered this long after the instant is missing.</summary>
    public int ProbeGraceMs { get; set; } = 2000;
    /// <summary>Extra time for a remote's PNG upload.</summary>
    public int UploadGraceMs { get; set; } = 3000;
    public TimeSpan PrefetchTimeout { get; set; } = TimeSpan.FromSeconds(120);
    public TimeSpan LogFetchTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public int DefaultStepTimeoutMs { get; set; } = 30000;
    public string ResultsRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaBiBaBuSy", "TestRuns");
    /// <summary>This machine's log folder; null = from the logging configuration.</summary>
    public string? LocalLogDirectory { get; set; }
    public Func<long> NowUtcMs { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    internal string ResolveLocalLogDirectory() =>
        LocalLogDirectory ?? ConfigurationManager.LoadLoggingConfiguration().LogDirectory;
}
