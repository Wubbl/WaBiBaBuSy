using Microsoft.Extensions.Logging.Abstractions;
using WaBiBaBuSy.Core.Services;
using WaBiBaBuSy.Core.Services.Networking;
using WaBiBaBuSy.Core.Services.Show;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Show;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>AGENDA: the codec (times shifted into the client's clock) and the client forwarding it without the timing wait.</summary>
public class AgendaTransportTests : IDisposable
{
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), $"wbbs-agenda-{Guid.NewGuid():N}");
    private readonly WallpaperSyncClient _client;
    private readonly WallpaperPlaybackService _playback;
    private readonly List<Agenda?> _received = new();

    public AgendaTransportTests()
    {
        _client = new WallpaperSyncClient(NullLogger<WallpaperSyncClient>.Instance, new ClientConfiguration());
        _playback = new WallpaperPlaybackService(NullLogger<WallpaperPlaybackService>.Instance, _client, cacheDirectory: _cacheDir)
        {
            AgendaDelegate = a => { lock (_received) _received.Add(a); return Task.CompletedTask; },
        };
    }

    public void Dispose()
    {
        _playback.Dispose();
        try { Directory.Delete(_cacheDir, recursive: true); } catch { /* best effort */ }
    }

    private static Agenda Sample() => new()
    {
        Enabled = true,
        Title = "BiBaBu LAN",
        Items = { new AgendaItem { StartUtcMs = 1_600_000, Text = "Pizza" } },
        Notes = { "WiFi: bibabu" },
        ScreenNodeIds = { "client-1" },
        UtcOffsetMinutes = 120,
    };

    [Fact]
    public void Codec_RoundTrip_KeepsItemsAndScreens()
    {
        var cmd = AgendaCodec.ToCommand(Sample(), 7, nowUtcMs: 1_000_000);
        Assert.Equal(CommandType.Agenda, cmd.Type);
        Assert.Equal(1_000_000, cmd.TimestampUtc);
        Assert.True(AgendaCodec.TryFromCommand(cmd, out var a));
        Assert.Equal("BiBaBu LAN", a.Title);
        Assert.Equal(1_600_000, Assert.Single(a.Items).StartUtcMs);
        Assert.Equal("WiFi: bibabu", Assert.Single(a.Notes));
        Assert.Equal("client-1", Assert.Single(a.ScreenNodeIds));
        Assert.Equal(120, a.UtcOffsetMinutes);
        Assert.True(a.Enabled);
    }

    [Fact]
    public void Codec_ShiftsTimesByTheConvertedTimestamp()
    {
        var cmd = AgendaCodec.ToCommand(Sample(), 1, nowUtcMs: 1_000_000);
        cmd.TimestampUtc -= 90_000;   // the client converted the server time into its own clock (90 s behind)
        Assert.True(AgendaCodec.TryFromCommand(cmd, out var a));
        Assert.Equal(1_510_000, a.Items[0].StartUtcMs);
    }

    [Fact]
    public void Codec_DoesNotChangeTheSentAgenda()
    {
        var sample = Sample();
        AgendaCodec.ToCommand(sample, 1, nowUtcMs: 1_000_000);
        Assert.Equal(0, sample.SentUtcMs);
    }

    [Fact]
    public void Codec_RejectsOtherTypesAndBadJson()
    {
        Assert.False(AgendaCodec.TryFromCommand(new SyncCommand { Type = CommandType.Event, AgendaJson = "{}" }, out _));
        Assert.False(AgendaCodec.TryFromCommand(new SyncCommand { Type = CommandType.Agenda, AgendaJson = "{nope" }, out _));
        Assert.False(AgendaCodec.TryFromCommand(new SyncCommand { Type = CommandType.Agenda }, out _));
    }

    [Fact]
    public async Task Client_Agenda_ForwardedWithoutWaiting()
    {
        var cmd = AgendaCodec.ToCommand(Sample(), 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000);   // a minute ahead
        await _playback.HandleAgendaAsync(cmd).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("BiBaBu LAN", Assert.Single(_received)!.Title);
    }

    [Fact]
    public async Task Client_Dispatch_RoutesAgendaToDelegate()
    {
        var cmd = AgendaCodec.ToCommand(Sample(), 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000);
        await _playback.Dispatch(cmd).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(_received);
    }
}
