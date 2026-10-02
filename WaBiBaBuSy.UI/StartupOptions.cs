using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WaBiBaBuSy.UI;

/// <summary>Command-line switches: <c>--test-run &lt;scenario.json&gt; [--exit]</c>.</summary>
public sealed class StartupOptions
{
    public const string Usage = "usage: WaBiBaBuSy.UI.exe [--test-run <scenario.json> [--exit]]";

    public string? TestRunPath { get; init; }
    public bool ExitWhenDone { get; init; }

    /// <summary>Arguments that were ignored, one line each (unknown switches, a missing value).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public static StartupOptions Current { get; private set; } = new();

    public static StartupOptions Parse(string[] args)
    {
        string? path = null;
        bool exit = false;
        var warnings = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--test-run", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) path = args[++i];
                else warnings.Add("--test-run needs a scenario file; ignored");
            }
            else if (args[i].Equals("--exit", StringComparison.OrdinalIgnoreCase)) exit = true;
            else warnings.Add($"unknown argument '{args[i]}' ignored");
        }
        if (exit && path == null) warnings.Add("--exit has no effect without --test-run");
        Current = new StartupOptions { TestRunPath = path, ExitWhenDone = exit, Warnings = warnings };
        return Current;
    }
}

/// <summary>
/// Lines for the user who started the app from a shell. The app is a WinExe, so <see cref="Console"/> output
/// goes nowhere unless redirected: each line is also written to the parent process's console, attached only
/// for that write (staying attached would let a later Ctrl+C in that shell kill the app). Note that cmd and
/// PowerShell do not wait for a GUI app: use <c>start /wait</c> or <c>Start-Process -Wait</c> for the exit code.
/// </summary>
internal static class CliConsole
{
    private const int StdOutputHandle = -11;
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;

    private static readonly object Gate = new();
    private static bool? _stdOutWasAbsent;

    /// <summary>Call once at startup, before anything attaches a console: redirected output needs no console write.</summary>
    public static void Initialize()
    {
        var handle = GetStdHandle(StdOutputHandle);
        _stdOutWasAbsent = handle == IntPtr.Zero || handle == new IntPtr(-1);
    }

    public static void WriteLine(string line, bool error = false)
    {
        (error ? Console.Error : Console.Out).WriteLine(line);   // a redirected stream or the log
        if (_stdOutWasAbsent != true) return;
        lock (Gate)
        {
            if (!AttachConsole(AttachParentProcess)) return;   // not started from a console
            try
            {
                var conOut = CreateFileW("CONOUT$", GenericWrite, FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (conOut == new IntPtr(-1)) return;
                try
                {
                    var text = Environment.NewLine + line + Environment.NewLine;
                    WriteConsoleW(conOut, text, (uint)text.Length, out _, IntPtr.Zero);
                }
                finally { CloseHandle(conOut); }
            }
            finally { FreeConsole(); }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WriteConsoleW(IntPtr hConsoleOutput, string lpBuffer, uint nNumberOfCharsToWrite, out uint lpNumberOfCharsWritten, IntPtr lpReserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
