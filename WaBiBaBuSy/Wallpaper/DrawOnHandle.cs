using GameOverlay.Drawing;
using GameOverlay.Windows;
//using SharpDX.Direct2D1;
//using SharpDX.DXGI;
using System;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Drawing;
using System.Text;

namespace WaBiBaBuSy.Wallpaper
{
    public class DrawOnHandle
    {
        private IntPtr _screenHandle;
        private GraphicsWindow _window;
        private OverlayWindow _overlayWindow;
        //private Graphics _graphics;

        public DrawOnHandle(IntPtr screenHandle)
        {
            _screenHandle = screenHandle;

            //_graphics = new Graphics(_screenHandle);
            //GraphicsWindow graphicsWindow = new GraphicsWindow(_graphics);

            //graphicsWindow.FPS = 60;
            //graphicsWindow.Create();
            //graphicsWindow.Join();
            DrawGameOverlayNETGraphicsWindow();           
        }

        public async void DrawBiBaBuLogoAnimation()
        {
            DrawGDI();
            //DrawDirect2DBiBaBuLogoAnimation();
            //DrawDirect2D();

            //_window.Create();
            //_window.Join();

            //DrawGameOverlayNETOverlayWindow();
        }

        public async void DrawGDI()
        {
            System.Drawing.Image bibabuImage = new System.Drawing.Bitmap(Properties.Resources.LogoBiBaBuColoring);
            System.Drawing.Graphics screenGrahpics = System.Drawing.Graphics.FromHwnd(_screenHandle);
            var screenSize = screenGrahpics.VisibleClipBounds;

            System.Drawing.Bitmap doubleBuffer = new System.Drawing.Bitmap((int)screenSize.Width, (int)screenSize.Height);
            System.Drawing.Graphics gBuffer = System.Drawing.Graphics.FromImage(doubleBuffer);

            for (int x = 100; x < (screenSize.Width - 1100); x += 6)
            {
                //screenGrahpics.DrawImage(bibabuImage, x, 100, 1000, 1000);
                gBuffer.Clear(System.Drawing.Color.Gray);
                gBuffer.DrawImage(bibabuImage, x, 100, 1000, 1000);
                gBuffer.Flush();

                screenGrahpics.DrawImageUnscaled(doubleBuffer, 0, 0);

                await Task.Delay(32);
            }
        }

        public async void DrawDirect2DBiBaBuLogoAnimation()
        {
            //Bitmap test = new Bitmap();

            //MemoryStream ms = new MemoryStream();
            //Properties.Resources.LogoBiBaBuColoring.Save(ms, ImageFormat.Png);
            //byte[] bitmapData = ms.ToArray();

            //Image bibabuImage = new Image(_graphics, bitmapData);
            
            //for (int x = 100; x < (_graphics.Width - 1100); x += 3)
            //{
            //    _graphics.ClearScene(new Color(128, 128, 128));
            //    _graphics.DrawImage(bibabuImage, x, 100);

            //    await Task.Delay(16);
            //}


            //CanvasImage image = new CanvasImage();

            //CanvasDevice device = new CanvasDevice();
            //CanvasRenderTarget canvasRenderTarget = new CanvasRenderTarget(device, 1200, 1200);

            //graphics.DrawImage()

            //Graphics screenGrahpics = Graphics.FromHwnd(_screenHandle);
            //var screenSize = screenGrahpics.VisibleClipBounds;

            //Bitmap doubleBuffer = new Bitmap((int)screenSize.Width, (int)screenSize.Height);
            //Graphics gBuffer = Graphics.FromImage(doubleBuffer);

            //for (int x = 100; x < (screenSize.Width - 1100); x += 3)
            //{
            //    //screenGrahpics.DrawImage(bibabuImage, x, 100, 1000, 1000);
            //    gBuffer.Clear(Color.Gray);
            //    gBuffer.DrawImage(bibabuImage, x, 100, 1000, 1000);
            //    gBuffer.Flush();

            //    screenGrahpics.DrawImageUnscaled(doubleBuffer, 0, 0);

            //    await Task.Delay(16);
            //}
        }

        public async void DrawGameOverlayNETOverlayWindow()
        {
            GameOverlay.Drawing.Graphics gfx = new GameOverlay.Drawing.Graphics(_screenHandle)
            {
                MeasureFPS = true,
                PerPrimitiveAntiAliasing = true,
                TextAntiAliasing = true,
            };

            _overlayWindow = new OverlayWindow(0, 0, 400, 400)
            {
                IsTopmost = true,
                IsVisible = true,
            };


            _overlayWindow.Create();
            //gfx.WindowHandle = _overlayWindow.Handle;
            gfx.Setup();
        }

        /// <summary>
        /// Uses NugetPackage GameOverlay.NET
        /// </summary>
        public async void DrawGameOverlayNETGraphicsWindow()
        {
            GameOverlay.Drawing.Graphics gfx = new GameOverlay.Drawing.Graphics()
            {
                MeasureFPS = true,
                PerPrimitiveAntiAliasing = true,
                TextAntiAliasing = true,
            };

            _window = new GraphicsWindow(0, 0, 800, 600, gfx)
            {
                FPS = 60,
                IsTopmost = true,
                IsVisible = true,
            };

            _window.SetupGraphics += _window_SetupGraphics;
            _window.DrawGraphics += _window_DrawGraphics;
        }

        private void _window_DrawGraphics(object? sender, DrawGraphicsEventArgs e)
        {
            var gfx = e.Graphics;

            var padding = 16;
            var infoText = new StringBuilder()
              .Append("FPS: ").Append(gfx.FPS.ToString().PadRight(padding))
              .Append("FrameTime: ").Append(e.FrameTime.ToString().PadRight(padding))
              .Append("FrameCount: ").Append(e.FrameCount.ToString().PadRight(padding))
              .Append("DeltaTime: ").Append(e.DeltaTime.ToString().PadRight(padding))
              .ToString();

            gfx.ClearScene(gfx.CreateSolidBrush(0,0,0));

            gfx.DrawTextWithBackground(gfx.CreateFont("Consolas", 14), gfx.CreateSolidBrush(0, 255, 0), gfx.CreateSolidBrush(0, 0, 0), 58, 20, infoText);

            gfx.DrawGeometry(_gridGeometry, gfx.CreateSolidBrush(255, 255, 255, 0.2f), 1.0f);
        }

        private GameOverlay.Drawing.Geometry _gridGeometry;
        private GameOverlay.Drawing.Rectangle _gridBounds;

        private void _window_SetupGraphics(object? sender, SetupGraphicsEventArgs e)
        {
            var gfx = e.Graphics;

            _gridBounds = new GameOverlay.Drawing.Rectangle(20, 60, gfx.Width - 20, gfx.Height - 20);
            _gridGeometry = gfx.CreateGeometry();

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
        }

        public void ClearWallpaper()
        {
            // Didn't find a way to Redraw / Invalidate the workerW screenHandle
            SetDesktopWallpaper(GetDesktopWallpaper());
        }

        static string GetDesktopWallpaper()
        {
            string wallpaper = new string('\0', Win32Imports.MAX_PATH);
            Win32Imports.SystemParametersInfo(Win32Imports.SPI_GETDESKWALLPAPER, wallpaper.Length, wallpaper, 0);
            return wallpaper.Substring(0, wallpaper.IndexOf('\0'));
        }

        static void SetDesktopWallpaper(string filename)
        {
            Win32Imports.SystemParametersInfo(Win32Imports.SPI_SETDESKWALLPAPER, 0, filename,
                Win32Imports.SPIF_UPDATEINIFILE | Win32Imports.SPIF_SENDWININICHANGE);
        }

        public static byte[] BitmapToByteArray(System.Drawing.Bitmap bitmap)
        {

            BitmapData bmpdata = null;

            try
            {
                bmpdata = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, bitmap.PixelFormat);
                int numbytes = bmpdata.Stride * bitmap.Height;
                byte[] bytedata = new byte[numbytes];
                IntPtr ptr = bmpdata.Scan0;

                Marshal.Copy(ptr, bytedata, 0, numbytes);

                return bytedata;
            }
            finally
            {
                if (bmpdata != null)
                    bitmap.UnlockBits(bmpdata);
            }

        }
    }
}
