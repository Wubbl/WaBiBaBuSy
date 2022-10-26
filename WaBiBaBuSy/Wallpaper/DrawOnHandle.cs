//using GameOverlay.Drawing;
//using GameOverlay.Windows;
//using SharpDX.Direct2D1;
//using SharpDX.DXGI;
using System;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Drawing;

namespace WaBiBaBuSy.Wallpaper
{
    public class DrawOnHandle
    {
        private IntPtr _screenHandle;
        //private Graphics _graphics;

        public DrawOnHandle(IntPtr screenHandle)
        {
            _screenHandle = screenHandle;

            //_graphics = new Graphics(_screenHandle);
            //GraphicsWindow graphicsWindow = new GraphicsWindow(_graphics);

            //graphicsWindow.FPS = 60;
            //graphicsWindow.Create();
            //graphicsWindow.Join();
        }

        public async void DrawBiBaBuLogoAnimation()
        {
            Image bibabuImage = new Bitmap(Properties.Resources.LogoBiBaBuColoring);
            Graphics screenGrahpics = Graphics.FromHwnd(_screenHandle);
            var screenSize = screenGrahpics.VisibleClipBounds;

            Bitmap doubleBuffer = new Bitmap((int)screenSize.Width, (int)screenSize.Height);
            Graphics gBuffer = Graphics.FromImage(doubleBuffer);

            for (int x = 100; x < (screenSize.Width - 1100); x += 6)
            {
                //screenGrahpics.DrawImage(bibabuImage, x, 100, 1000, 1000);
                gBuffer.Clear(Color.Gray);
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
