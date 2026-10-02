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
    private readonly Func<Process[]> _listPlayers;

    /// <summary>Samples this app and every running <see cref="PlayerProcessName"/> process.</summary>
    public PerfSampler() : this(() => Process.GetProcessesByName(PlayerProcessName)) { }

    /// <summary>Samples this app and the processes <paramref name="listPlayers"/> returns (disposed after each sample).</summary>
    public PerfSampler(Func<Process[]> listPlayers)
    {
        _listPlayers = listPlayers;
    }

    /// <summary>Processes with a CPU baseline (this app + the players of the last sample).</summary>
    public int TrackedProcessCount
    {
        get { lock (_lock) return _lastCpu.Count; }
    }

    public PerfSample Sample()
    {
        lock (_lock)
            return SampleLocked();
    }

    private PerfSample SampleLocked()
    {
        using var app = Process.GetCurrentProcess();
        var players = _listPlayers();
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
            // Every scene change starts new players: forget the baselines of the ones that are gone.
            var live = players.Select(p => p.Id).Append(app.Id).ToHashSet();
            foreach (var pid in _lastCpu.Keys.Where(pid => !live.Contains(pid)).ToList())
                _lastCpu.Remove(pid);
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
        if (playerPids.Count == 0)
        {
            DisposeGpuCountersExcept(Array.Empty<string>());
            return null;
        }
        try
        {
            var prefixes = playerPids.Select(pid => $"pid_{pid}_").ToList();
            var names = new PerformanceCounterCategory("GPU Engine").GetInstanceNames()
                .Where(n => n.Contains("engtype_3D", StringComparison.Ordinal) && prefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
                .ToList();
            DisposeGpuCountersExcept(names);

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

    /// <summary>Release the counter handles of engine instances that no longer exist (exited players).</summary>
    private void DisposeGpuCountersExcept(IReadOnlyCollection<string> liveNames)
    {
        foreach (var name in _gpuCounters.Keys.Where(n => !liveNames.Contains(n)).ToList())
        {
            _gpuCounters[name].Dispose();
            _gpuCounters.Remove(name);
        }
    }
}
