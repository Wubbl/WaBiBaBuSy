using System.Runtime.InteropServices;
using WaBiBaBuSy.Core.Services.Update;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>Review 2026-10-13 E1: the updater's paths must survive Windows command-line parsing.</summary>
public class UpdateApplicatorArgsTests
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string cmdLine, out int argc);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    /// <summary>How the updater's Main would receive the line (CommandLineToArgvW = the CRT rules).</summary>
    private static string[] Parse(string arguments)
    {
        var argv = CommandLineToArgvW("updater.exe " + arguments, out int argc);
        try
        {
            return Enumerable.Range(1, argc - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray();
        }
        finally { LocalFree(argv); }
    }

    [Theory]
    [InlineData(@"C:\Program Files\WaBiBaBuSy\")]   // AppContext.BaseDirectory: always ends in '\'
    [InlineData(@"C:\Program Files\WaBiBaBuSy")]
    [InlineData(@"C:\a b\c\\")]
    public void QuotedPaths_RoundTrip(string installDir)
    {
        string line = $"--install-dir {UpdateApplicator.QuoteArg(installDir)} --backup-dir {UpdateApplicator.QuoteArg(@"C:\Backup")} --process-id 42";
        Assert.Equal(new[] { "--install-dir", installDir, "--backup-dir", @"C:\Backup", "--process-id", "42" }, Parse(line));
        Assert.Equal(Parse(line), WaBiBaBuSy.Updater.LegacyArgs.Repair(Parse(line)));   // well-formed args pass unchanged
    }

    /// <summary>Old clients send a placeholder id with the update download; the server finds them by this IP.</summary>
    [Theory]
    [InlineData("ipv4:192.168.1.5:50123", "192.168.1.5")]
    [InlineData("ipv6:[::ffff:192.168.1.5]:50123", "192.168.1.5")]
    [InlineData("ipv6:[fe80::1]:50123", "fe80::1")]
    public void PeerIp_IsTheHost(string peer, string ip) =>
        Assert.Equal(ip, WaBiBaBuSy.Grpc.Services.WallpaperSyncService.PeerIp(peer));

    /// <summary>The exact line a 2.6.3 client builds (plain quotes around a path ending in '\') is repaired by the updater.</summary>
    [Fact]
    public void LegacyClientLine_IsRepaired()
    {
        string line = @"--update-dir ""C:\Users\Jo Doe\AppData\Local\WaBiBaBuSy\Updates\Extract_2.7.5"" " +
                      @"--install-dir ""C:\Program Files\WaBiBaBuSy\"" " +
                      @"--backup-dir ""C:\Users\Jo Doe\AppData\Local\WaBiBaBuSy\Backups"" " +
                      "--process-id 1234";
        Assert.Equal(5, Parse(line).Length);   // the bug: --backup-dir and --process-id swallowed by --install-dir

        Assert.Equal(new[]
        {
            "--update-dir", @"C:\Users\Jo Doe\AppData\Local\WaBiBaBuSy\Updates\Extract_2.7.5",
            "--install-dir", @"C:\Program Files\WaBiBaBuSy",
            "--backup-dir", @"C:\Users\Jo Doe\AppData\Local\WaBiBaBuSy\Backups",
            "--process-id", "1234"
        }, WaBiBaBuSy.Updater.LegacyArgs.Repair(Parse(line)));
    }
}
