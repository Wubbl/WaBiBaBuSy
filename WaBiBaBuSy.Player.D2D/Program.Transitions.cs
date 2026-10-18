// WaBiBaBuSy.Player.D2D/Program.Transitions.cs
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Vortice.Mathematics;
using WaBiBaBuSy.Models.Topology;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.Player.Common.Messages;

namespace WaBiBaBuSy.Player.D2D;

/// <summary>
/// Playlist item transitions (roadmap 2.5) on top of the standby switch: old and new item are separate player
/// processes, the new one's window above. Fade = each darkens its own frame (window alpha is ignored for a flip-model
/// swap chain); Wipe = the incoming window is clipped to the part of the room the boundary has passed (window
/// regions are honored). Math in <see cref="TransitionMath"/>; everything follows the shared clock.
/// </summary>
partial class Program
{
    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    // Set by the command thread, read by the render thread (whole-object swaps).
    private static volatile SceneTransition? _transitionIn;
    private static volatile SceneTransition? _transitionOut;
    private static long _transitionStartUtcMs;
    private static (int L, int R)? _wipeRegion;   // render thread only: last region set, null = none

    private static void HandleTransitionCommand(PlayerCommandTransition cmd)
    {
        var t = SceneTransition.Parse(cmd.Transition);
        Interlocked.Exchange(ref _transitionStartUtcMs, cmd.StartUtcMs);
        if (cmd.Incoming) _transitionIn = t.IsCut ? null : t;
        else _transitionOut = t.IsCut ? null : t;
        _logger?.LogInformation("[Transition] {Role} {Transition} at {Start} ({In} ms from now)",
            cmd.Incoming ? "in" : "out", t, cmd.StartUtcMs, cmd.StartUtcMs - NowUtcMs);
    }

    /// <summary>Render thread, before a frame: keep the incoming window clipped to the wiped part (Wipe).</summary>
    private static void UpdateWipeRegion()
    {
        var t = _transitionIn;
        if (t?.Kind != TransitionKind.Wipe || _hwnd == IntPtr.Zero)
        {
            if (_wipeRegion != null) { SetWindowRgn(_hwnd, IntPtr.Zero, true); _wipeRegion = null; }
            return;
        }
        long ms = NowUtcMs - Interlocked.Read(ref _transitionStartUtcMs);
        if (ms >= t.ClampedMs)
        {
            SetWindowRgn(_hwnd, IntPtr.Zero, true);   // done: the whole window
            _wipeRegion = null;
            _transitionIn = null;
            return;
        }
        // The room's path, whatever the scene's distribution; without a seat map this screen alone.
        var node = _sequentialLayout ?? new NodeLayout { Width = _width, CanvasWidth = _width, Scale = 1f };
        float canvas = node.CanvasWidth > 0 ? node.CanvasWidth : node.Width;
        var (l, r) = TransitionMath.WipedSpan(TransitionMath.WipeBoundary(ms, t.ClampedMs, canvas), node);
        float scale = node.Scale > 0 ? node.Scale : 1f;
        int left = (int)MathF.Floor(l * scale), right = (int)MathF.Ceiling(r * scale);
        if (right <= left) left = right = 0;
        if (_wipeRegion is { } cur && cur.L == left && cur.R == right) return;
        SetWindowRgn(_hwnd, CreateRectRgn(left, 0, right, _height), true);   // the system owns the region now
        _wipeRegion = (left, right);
    }

    /// <summary>Render thread, inside BeginDraw: the Fade veil (black) over the whole frame, if any.</summary>
    private static void DrawTransitionVeil()
    {
        if (_d2dContext == null) return;
        var tin = _transitionIn;
        var tout = _transitionOut;
        if (tin?.Kind != TransitionKind.Fade && tout?.Kind != TransitionKind.Fade) return;
        long ms = NowUtcMs - Interlocked.Read(ref _transitionStartUtcMs);
        float veil = 0f;
        if (tin?.Kind == TransitionKind.Fade)
        {
            veil = TransitionMath.FadeInVeil(ms, tin.ClampedMs);
            if (veil <= 0f && ms > 0) _transitionIn = null;   // fully in
        }
        if (tout?.Kind == TransitionKind.Fade) veil = Math.Max(veil, TransitionMath.FadeOutVeil(ms, tout.ClampedMs));
        if (veil <= 0.003f) return;

        _eventBrush ??= _d2dContext.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f));
        _eventBrush.Color = new Color4(0f, 0f, 0f, 1f);
        _eventBrush.Opacity = Math.Clamp(veil, 0f, 1f);
        var prior = _d2dContext.Transform;
        _d2dContext.Transform = Matrix3x2.Identity;
        _d2dContext.FillRectangle(new System.Drawing.RectangleF(0, 0, _width, _height), _eventBrush);
        _d2dContext.Transform = prior;
    }
}
