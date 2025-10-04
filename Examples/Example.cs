using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

using GameOverlay.Drawing;
using GameOverlay.Windows;

namespace Examples
{
    public class Example : IDisposable
    {
        private readonly Graphics _gfx;
        private readonly GraphicsWindow _window;
        private readonly IntPtr _screenHandle;
        private readonly IntPtr _progmanHandle;

        private readonly Dictionary<string, SolidBrush> _brushes;
        private readonly Dictionary<string, Font> _fonts;
        private readonly Dictionary<string, Image> _images;

        private Geometry _gridGeometry;
        private Rectangle _gridBounds;

        private Random _random;
        private long _lastRandomSet;
        private List<Action<Graphics, float, float>> _randomFigures;

        internal class Win32Imports
        {
            public static readonly int MAX_PATH = 260;
            public static readonly uint SPI_GETDESKWALLPAPER = 0x73;
            public static readonly uint SPI_SETDESKWALLPAPER = 0x14;
            public static readonly uint SPIF_UPDATEINIFILE = 0x01;
            public static readonly uint SPIF_SENDWININICHANGE = 0x02;

            public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

            [Flags]
            public enum SendMessageTimeoutFlags : uint
            {
                SMTO_NORMAL = 0x0,
                SMTO_BLOCK = 0x1,
                SMTO_ABORTIFHUNG = 0x2,
                SMTO_NOTIMEOUTIFNOTHUNG = 0x8,
                SMTO_ERRORONEXIT = 0x20
            }

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, IntPtr windowTitle);

            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, int wParam, IntPtr lParam);

            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
            public static extern IntPtr SendMessageTimeout(IntPtr windowHandle, uint Msg, IntPtr wParam, IntPtr lParam, SendMessageTimeoutFlags flags, uint timeout, out IntPtr result);

            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            public static extern Int32 SystemParametersInfo(UInt32 action, int uParam, string vParam, UInt32 winIni);
        }

        public static IntPtr FindWindowHandle()
        {
            // Fetch the Progman window
            IntPtr progman = Win32Imports.FindWindow("Progman", null);

            // Send 0x052C to Progman. This message directs Progman to spawn a 
            // WorkerW behind the desktop icons. If it is already there, nothing 
            // happens.
            Win32Imports.SendMessageTimeout(progman,
                                   0x052C,
                                   new IntPtr(0),
                                   IntPtr.Zero,
                                   Win32Imports.SendMessageTimeoutFlags.SMTO_NORMAL,
                                   1000,
                                   out _);

            IntPtr workerw = IntPtr.Zero;

            // We enumerate all Windows, until we find one, that has the SHELLDLL_DefView 
            // as a child. 
            // If we found that window, we take its next sibling and assign it to workerw.
            Win32Imports.EnumWindows(new Win32Imports.EnumWindowsProc((tophandle, topparamhandle) =>
            {
                IntPtr p = Win32Imports.FindWindowEx(tophandle,
                                            IntPtr.Zero,
                                            "SHELLDLL_DefView",
                                            IntPtr.Zero);

                if (p != IntPtr.Zero)
                {
                    // Gets the WorkerW Window after the current one.
                    workerw = Win32Imports.FindWindowEx(IntPtr.Zero,
                                               tophandle,
                                               "WorkerW",
                                               IntPtr.Zero);
                }

                return true;
            }), IntPtr.Zero);

            return workerw;
        }

        public Example()
        {
            _brushes = new Dictionary<string, SolidBrush>();
            _fonts = new Dictionary<string, Font>();
            _images = new Dictionary<string, Image>();

            _screenHandle = FindWindowHandle();
            _progmanHandle = Win32Imports.FindWindow("Progman", null);

            _gfx = new Graphics(_screenHandle)
            {
                MeasureFPS = true,
                PerPrimitiveAntiAliasing = true,
                TextAntiAliasing = true
            };

            _window = new GraphicsWindow(0, 0, 800, 600, _gfx)
            {
                FPS = 60,
                IsTopmost = false,
                IsVisible = true
            };

            _window.PlaceAbove(_screenHandle);

            _window.DestroyGraphics += _window_DestroyGraphics;
            _window.DrawGraphics += _window_DrawGraphics;
            _window.SetupGraphics += _window_SetupGraphics;

            //_overlay = new OverlayWindow(0, 0, 400, 400)
            //{
            //    IsTopmost = false,
            //    IsVisible = true,
            //};

            //_overlay.PlaceAbove(_screenHandle);
            //_gfx.WindowHandle = _screenHandle;
            //_overlay.Create();
        }

        private void _window_SetupGraphics(object sender, SetupGraphicsEventArgs e)
        {
            if (e.RecreateResources)
            {
                foreach (var pair in _brushes) pair.Value.Dispose();
                foreach (var pair in _images) pair.Value.Dispose();
            }

            _brushes["black"] = _gfx.CreateSolidBrush(0, 0, 0);
            _brushes["white"] = _gfx.CreateSolidBrush(255, 255, 255);
            _brushes["red"] = _gfx.CreateSolidBrush(255, 0, 0);
            _brushes["green"] = _gfx.CreateSolidBrush(0, 255, 0);
            _brushes["blue"] = _gfx.CreateSolidBrush(0, 0, 255);
            _brushes["background"] = _gfx.CreateSolidBrush(0x33, 0x36, 0x3F);
            _brushes["grid"] = _gfx.CreateSolidBrush(255, 255, 255, 0.2f);
            _brushes["random"] = _gfx.CreateSolidBrush(0, 0, 0);

            if (e.RecreateResources) return;

            _fonts["arial"] = _gfx.CreateFont("Arial", 12);
            _fonts["consolas"] = _gfx.CreateFont("Consolas", 14);

            _gridBounds = new Rectangle(20, 60, _gfx.Width - 20, _gfx.Height - 20);
            _gridGeometry = _gfx.CreateGeometry();

            for (float x = _gridBounds.Left; x <= _gridBounds.Right; x += 20)
            {
                var line = new Line(x, _gridBounds.Top, x, _gridBounds.Bottom);
                _gridGeometry.BeginFigure(line);
                _gridGeometry.EndFigure(false);
            }

            for (float y = _gridBounds.Top; y <= _gridBounds.Bottom; y += 20)
            {
                var line = new Line(_gridBounds.Left, y, _gridBounds.Right, y);
                _gridGeometry.BeginFigure(line);
                _gridGeometry.EndFigure(false);
            }

            _gridGeometry.Close();

            _randomFigures = new List<Action<Graphics, float, float>>()
      {
        (g, x, y) => g.DrawRectangle(GetRandomColor(), x + 10, y + 10, x + 110, y + 110, 2.0f),
        (g, x, y) => g.DrawCircle(GetRandomColor(), x + 60, y + 60, 48, 2.0f),
        (g, x, y) => g.DrawRoundedRectangle(GetRandomColor(), x + 10, y + 10, x + 110, y + 110, 8.0f, 2.0f),
        (g, x, y) => g.DrawTriangle(GetRandomColor(), x + 10, y + 110, x + 110, y + 110, x + 60, y + 10, 2.0f),
        (g, x, y) => g.DashedRectangle(GetRandomColor(), x + 10, y + 10, x + 110, y + 110, 2.0f),
        (g, x, y) => g.DashedCircle(GetRandomColor(), x + 60, y + 60, 48, 2.0f),
        (g, x, y) => g.DashedRoundedRectangle(GetRandomColor(), x + 10, y + 10, x + 110, y + 110, 8.0f, 2.0f),
        (g, x, y) => g.DashedTriangle(GetRandomColor(), x + 10, y + 110, x + 110, y + 110, x + 60, y + 10, 2.0f),
        (g, x, y) => g.FillRectangle(GetRandomColor(), x + 10, y + 10, x + 110, y + 110),
        (g, x, y) => g.FillCircle(GetRandomColor(), x + 60, y + 60, 48),
        (g, x, y) => g.FillRoundedRectangle(GetRandomColor(), x + 10, y + 10, x + 110, y + 110, 8.0f),
        (g, x, y) => g.FillTriangle(GetRandomColor(), x + 10, y + 110, x + 110, y + 110, x + 60, y + 10),
      };
        }

        private void _window_DestroyGraphics(object sender, DestroyGraphicsEventArgs e)
        {
            foreach (var pair in _brushes) pair.Value.Dispose();
            foreach (var pair in _fonts) pair.Value.Dispose();
            foreach (var pair in _images) pair.Value.Dispose();
        }

        private void _window_DrawGraphics(object sender, DrawGraphicsEventArgs e)
        {
            _window.PlaceAbove(_screenHandle);

            var padding = 16;
            var infoText = new StringBuilder()
              .Append("FPS: ").Append(_gfx.FPS.ToString().PadRight(padding))
              .Append("FrameTime: ").Append(e.FrameTime.ToString().PadRight(padding))
              .Append("FrameCount: ").Append(e.FrameCount.ToString().PadRight(padding))
              .Append("DeltaTime: ").Append(e.DeltaTime.ToString().PadRight(padding))
              .ToString();

            _gfx.ClearScene(_brushes["background"]);

            _gfx.DrawTextWithBackground(_fonts["consolas"], _brushes["green"], _brushes["black"], 58, 20, infoText);

            _gfx.DrawGeometry(_gridGeometry, _brushes["grid"], 1.0f);

            if (_lastRandomSet == 0L || e.FrameTime - _lastRandomSet > 2500)
            {
                _lastRandomSet = e.FrameTime;
            }

            _random = new Random(unchecked((int)_lastRandomSet));

            for (float row = _gridBounds.Top + 12; row < _gridBounds.Bottom - 120; row += 120)
            {
                for (float column = _gridBounds.Left + 12; column < _gridBounds.Right - 120; column += 120)
                {
                    DrawRandomFigure(_gfx, column, row);
                }
            }
        }

        private void DrawRandomFigure(Graphics gfx, float x, float y)
        {
            var action = _randomFigures[_random.Next(0, _randomFigures.Count)];

            action(gfx, x, y);
        }

        private SolidBrush GetRandomColor()
        {
            var brush = _brushes["random"];

            brush.Color = new Color(_random.Next(0, 256), _random.Next(0, 256), _random.Next(0, 256));

            return brush;
        }

        public void Run()
        {
            _window.Create();
            _window.Join();
        }

        ~Example()
        {
            Dispose(false);
        }

        #region IDisposable Support
        private bool disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                _window.Dispose();

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
