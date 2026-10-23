using Avalonia.Input;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using WaBiBaBuSy.Client.Linux.Rendering;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.Player.Common.Messages;
using Xunit;
using static WaBiBaBuSy.Client.Linux.Tests.WallpaperSceneTests;

namespace WaBiBaBuSy.Client.Linux.Tests;

/// <summary>Show events, BiBaBo and the agenda on a Linux monitor (offscreen renders).</summary>
public class ShowEventTests
{
    private const int W = 480, H = 270;

    private static WallpaperScene Seat(int order, int seats, string bg = "#000000")
    {
        var scene = new WallpaperScene(W, H, NullLogger.Instance, "test-machine");
        scene.Load(LoadCmd(WallpaperSceneTests.Seat(seats * W, order * W, W, H, order, $"c{order}"), bg));
        scene.Start(new PlayerCommandStartAnimation { StartTimestampMs = 1000 });
        return scene;
    }

    [Fact]
    public void Blackout_CoversTheSeatInItsColour()
    {
        using var scene = Seat(0, 1, "#336699");
        scene.AddShowEvent(new ShowEvent { EventId = "b1", Type = ShowEventType.Blackout, T0Utc = 2000, DurationMs = 4000, ColorHex = "#FF0000" });
        using var before = RenderAt(scene, 1900);
        using var during = RenderAt(scene, 4000);
        Assert.Equal(new SKColor(0x33, 0x66, 0x99), before.GetPixel(10, 10));
        Assert.Equal(SKColors.Red, during.GetPixel(10, 10));
    }

    [Fact]
    public void TargetedEvent_OnlyDrawsOnItsSeat()
    {
        using var seat0 = Seat(0, 2, "#336699");
        using var seat1 = Seat(1, 2, "#336699");
        var evt = new ShowEvent { EventId = "b2", Type = ShowEventType.Blackout, T0Utc = 2000, DurationMs = 4000, ColorHex = "#FF0000", TargetNodeIds = new() { "c1" } };
        seat0.AddShowEvent(evt);
        seat1.AddShowEvent(evt);
        using var a = RenderAt(seat0, 4000);
        using var b = RenderAt(seat1, 4000);
        Assert.Equal(new SKColor(0x33, 0x66, 0x99), a.GetPixel(10, 10));
        Assert.Equal(SKColors.Red, b.GetPixel(10, 10));
    }

    [Fact]
    public void EveryEventType_Renders()
    {
        using var scene = Seat(0, 2);
        var types = new[] { ShowEventType.Spotlight, ShowEventType.Celebration, ShowEventType.Announce, ShowEventType.Identify, ShowEventType.Traversal };
        for (int i = 0; i < types.Length; i++)
            scene.AddShowEvent(new ShowEvent { EventId = $"e{i}", Type = types[i], T0Utc = 2000, DurationMs = 8000, Seed = i, Text = "Hello", TargetOrder = 1 });
        Thread.Sleep(300);   // text sprites render on a worker
        for (long t = 1500; t < 10_500; t += 250)
            using (RenderAt(scene, t)) { }
    }

    /// <summary>A whole BiBaBo game over three seats, every mode: intro, cables, cards, timer, keypad, outcome.</summary>
    [Theory]
    [InlineData(BombMode.Classic, BombResult.WrongCable)]
    [InlineData(BombMode.CutOrder, BombResult.Defused)]
    [InlineData(BombMode.Keypad, BombResult.Timeout)]
    public void BiBaBo_FullGame_Renders(BombMode mode, BombResult result)
    {
        const int seats = 3;
        var nodes = Enumerable.Range(0, seats).Select(i => WallpaperSceneTests.Seat(seats * W, i * W, W, H, i, $"c{i}")).ToList();
        var layout = new SeatMapLayoutResult { Nodes = nodes, CanvasWidth = seats * W, CanvasHeight = H, Traversal = TraversalMode.Snake };
        var (game, _) = BombGameFactory.Create(new BombGameRequest(layout, null, _ => false, 42, "g1", TimerMs: 60_000,
            ForceMode: mode, ForceJunction: true, ForceRuleCard: true, Riddles: 4), out var error);
        Assert.True(game != null, error);

        var scenes = Enumerable.Range(0, seats).Select(i => Seat(i, seats)).ToList();
        try
        {
            var bomb = new ShowEvent { EventId = "g1", Type = ShowEventType.Bomb, T0Utc = 2000, DurationMs = game!.IntroMs + game.TimerMs + 15_000, Bomb = game };
            foreach (var s in scenes) s.AddShowEvent(bomb);
            Thread.Sleep(500);   // text sprites render on a worker
            long playing = 2000 + game.IntroMs + 2000;
            foreach (var t in new long[] { 2100, 2000 + 1500, 2000 + game.IntroMs / 2, playing })
                foreach (var s in scenes) using (RenderAt(s, t)) { }

            // Once the timer runs every seat is black with the corner timer in red
            int bombSeat = nodes.FindIndex(n => n.Id == game.BombNodeId);
            using (var shot = RenderAt(scenes[bombSeat], playing))
                Assert.Equal(SKColors.Black, shot.GetPixel(2, 2));

            var outcomeGame = SceneFile_Clone(game);
            outcomeGame.Result = result;
            outcomeGame.RemainingMs = 30_000;
            var outcome = new ShowEvent { EventId = "g1-out", Type = ShowEventType.BombOutcome, T0Utc = playing + 1000, DurationMs = 8000, Seed = 3, Bomb = outcomeGame };
            foreach (var s in scenes) s.AddShowEvent(outcome);
            Thread.Sleep(300);
            for (long t = playing + 900; t < playing + 9500; t += 400)
                foreach (var s in scenes) using (RenderAt(s, t)) { }
        }
        finally
        {
            foreach (var s in scenes) s.Dispose();
        }
    }

    /// <summary>An outcome event carries its own copy of the game (the server sends a new event).</summary>
    private static BombGame SceneFile_Clone(BombGame g) =>
        System.Text.Json.JsonSerializer.Deserialize<BombGame>(System.Text.Json.JsonSerializer.Serialize(g))!;

    [Fact]
    public void BiBaBo_ClickOnATerminal_CutsThatCable()
    {
        // What the Linux window reports: device pixels of the seat → BombHit, the same geometry the painter draws
        var nodes = Enumerable.Range(0, 3).Select(i => WallpaperSceneTests.Seat(3 * W, i * W, W, H, i, $"c{i}")).ToList();
        var layout = new SeatMapLayoutResult { Nodes = nodes, CanvasWidth = 3 * W, CanvasHeight = H, Traversal = TraversalMode.Snake };
        var (game, _) = BombGameFactory.Create(new BombGameRequest(layout, null, _ => false, 7, "g2", ForceMode: BombMode.Classic), out _);
        var node = nodes.Single(n => n.Id == game!.BombNodeId);
        for (int c = 0; c < game!.Cables.Count; c++)
        {
            var p = game.Cables[c].Points;
            float x = NodeMapping.ToLocalX(p[0], 0f, node), y = p[1] - node.OffsetY;
            Assert.Equal(c, BombHit.CableAt(game, node, x, y));
        }
    }

    [Fact]
    public void Agenda_PanelIsDrawn_AndBlackoutHidesIt()
    {
        using var scene = Seat(0, 1, "#336699");
        var agenda = new Agenda
        {
            Enabled = true, Title = "LAN", Corner = AgendaCorner.TopRight,
            Items = { new AgendaItem { StartUtcMs = 5000, Text = "Pizza" } },
        };
        scene.SetAgenda(agenda, fullScreen: false);
        using var shot = RenderAt(scene, 3000);
        var layout = AgendaLayout.Panel(W, H, AgendaCorner.TopRight, hasTitle: true, rows: 1, noteLines: 0);
        var card = shot.GetPixel((int)(layout.Card.X + 2), (int)(layout.Card.Y + layout.Card.H - 2));
        Assert.NotEqual(new SKColor(0x33, 0x66, 0x99), card);   // the dark card over the background

        scene.SetAgenda(agenda, fullScreen: true);
        using (RenderAt(scene, 3000)) { }

        scene.AddShowEvent(new ShowEvent { EventId = "bo", Type = ShowEventType.Blackout, T0Utc = 3500, DurationMs = 4000, ColorHex = "#000000" });
        using var dark = RenderAt(scene, 5500);
        Assert.Equal(SKColors.Black, dark.GetPixel((int)(layout.Card.X + 2), (int)(layout.Card.Y + layout.Card.H - 2)));
    }

    [Fact]
    public void TextSprite_RendersVisibleText()
    {
        using var img = TextSprite.Render("BOOM!", "#FF6D00");
        Assert.True(img.Width > img.Height);
        using var bmp = SKBitmap.FromImage(img);
        bool any = false;
        for (int y = 0; y < bmp.Height && !any; y += 4)
            for (int x = 0; x < bmp.Width && !any; x += 4)
                any = bmp.GetPixel(x, y).Alpha > 128;
        Assert.True(any);
    }

    [Theory]
    [InlineData(Key.D0, 0)]
    [InlineData(Key.D7, 7)]
    [InlineData(Key.NumPad3, 3)]
    [InlineData(Key.Back, 10)]
    [InlineData(Key.Delete, 10)]
    [InlineData(Key.C, 10)]
    [InlineData(Key.Left, -1)]
    [InlineData(Key.A, -1)]
    public void BombKeys_MapLikeTheWindowsSink(Key key, int expected) => Assert.Equal(expected, BombKeys.FromKey(key));
}
