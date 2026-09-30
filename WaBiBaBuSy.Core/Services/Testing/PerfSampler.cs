using System.Diagnostics;
using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.Core.Services.Testing;

/// <summary>
/// CPU / memory of this app and its D2D player processes, plus the players' 3D-engine GPU
/// utilisation from the "GPU Engine" performance counters. CPU% and GPU% need two samples;
/// the first sample reports null for them.
/// </summary>
public sealed class PerfSampler
{
    public const string PlayerProcessName = "WaBiBaBuSy.Player.D2D";

    private readonly Dictionary<int, (TimeSpan Cpu, long Tick)> _lastCpu = new();
    private readonly Dictionary<string, PerformanceCounter> _gpuCounters = new();
    private readonly object _lock = new();

    public PerfSample Sample()
    {
        lock (_lock)
            return SampleLocked();
    }

    private PerfSample SampleLocked()
    {
        using var app = Process.GetCurrentProcess();
        var players = Process.GetProcessesByName(PlayerProcessName);
        try
        {
            double? playerCpu = null;
            double playerMemory = 0;
            foreach (var p in players)
            {
                var cpu = CpuPercent(p);
                if (cpu.HasValue) playerCpu = (playerCpu ?? 0) + cpu.Value;
                playerMemory += p.WorkingSet64 / (1024.0 * 1024.0);
            }
            return new PerfSample
            {
                AppCpuPercent = CpuPercent(app),
                AppMemoryMb = app.WorkingSet64 / (1024.0 * 1024.0),
                PlayerCpuPercent = playerCpu,
                PlayerMemoryMb = playerMemory,
                PlayerProcesses = players.Length,
                GpuPercent = GpuPercent(players.Select(p => p.Id).ToList()),
            };
        }
        finally
        {
            foreach (var p in players) p.Dispose();
        }
    }

    private double? CpuPercent(Process process)
    {
        try
        {
            process.Refresh();
            long now = Environment.TickCount64;
            var cpu = process.TotalProcessorTime;
            double? result = null;
            if (_lastCpu.TryGetValue(process.Id, out var previous) && now > previous.Tick)
                result = (cpu - previous.Cpu).TotalMilliseconds / (now - previous.Tick) / Environment.ProcessorCount * 100.0;
            _lastCpu[process.Id] = (cpu, now);
            return result;
        }
        catch (Exception)
        {
            return null;   // process exited between listing and reading
        }
    }

    private double? GpuPercent(IReadOnlyList<int> playerPids)
    {
        if (playerPids.Count == 0) return null;
        try
        {
            var prefixes = playerPids.Select(pid => $"pid_{pid}_").ToList();
            var names = new PerformanceCounterCategory("GPU Engine").GetInstanceNames()
                .Where(n => n.Contains("engtype_3D", StringComparison.Ordinal) && prefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
                .ToList();

            double total = 0;
            bool anyWarm = false;
            foreach (var name in names)
            {
                if (!_gpuCounters.TryGetValue(name, out var counter))
                {
                    counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, readOnly: true);
                    counter.NextValue();   // first read of a rate counter is always 0
                    _gpuCounters[name] = counter;
                    continue;
                }
                total += counter.NextValue();
                anyWarm = true;
            }
            return anyWarm ? total : null;
        }
        catch (Exception)
        {
            return null;   // counters unavailable (no WDDM 2.x driver, locked-down machine)
        }
    }
}
