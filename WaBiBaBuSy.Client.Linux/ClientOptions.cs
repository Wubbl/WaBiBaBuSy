using WaBiBaBuSy.Client.Linux.Platform;

namespace WaBiBaBuSy.Client.Linux;

/// <summary>Command-line options for the headless Linux client.</summary>
public sealed class ClientOptions
{
    public string? ServerAddress { get; private set; }
    public int ServerPort { get; private set; } = 50051;
    public WindowPlacement Placement { get; private set; } = WindowPlacement.Desktop;
    public bool Demo { get; private set; }
    public bool Verbose { get; private set; }
    public bool SoftwareRendering { get; private set; }
    public string? LogDirectory { get; private set; }
    public bool ShowHelp { get; private set; }

    public const string Usage = """
        Usage: wabibabusy-client [options]

          --server HOST[:PORT]   Connect to this server instead of mDNS discovery (default port 50051)
          --placement MODE       desktop (default) | below | normal
                                   desktop: behind every window, like a real wallpaper
                                   below:   use if 'desktop' ends up hidden behind your desktop shell
                                   normal:  ordinary window, for debugging
          --demo                 Show a local test animation (no server) to check window placement
          --software             Render on the CPU (use if the GPU driver shows a black/garbled wallpaper)
          --log-dir DIR          Also write logs to DIR
          --verbose              Debug-level logging
          -h, --help             Show this help
        """;

    public static ClientOptions Parse(string[] args)
    {
        var o = new ClientOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--server":
                {
                    var value = Next();
                    int colon = value.LastIndexOf(':');
                    if (colon > 0 && int.TryParse(value[(colon + 1)..], out var port))
                    {
                        o.ServerAddress = value[..colon];
                        o.ServerPort = port;
                    }
                    else
                    {
                        o.ServerAddress = value;
                    }
                    break;
                }
                case "--placement":
                    o.Placement = Next().ToLowerInvariant() switch
                    {
                        "desktop" => WindowPlacement.Desktop,
                        "below" => WindowPlacement.Below,
                        "normal" => WindowPlacement.Normal,
                        var other => throw new ArgumentException($"Unknown placement '{other}'")
                    };
                    break;
                case "--demo": o.Demo = true; break;
                case "--software": o.SoftwareRendering = true; break;
                case "--log-dir": o.LogDirectory = Next(); break;
                case "--verbose": o.Verbose = true; break;
                case "-h" or "--help": o.ShowHelp = true; break;
                default: throw new ArgumentException($"Unknown option '{args[i]}'");
            }
        }
        return o;
    }
}
