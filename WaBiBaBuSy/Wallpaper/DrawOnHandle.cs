using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WaBiBaBuSy.Wallpaper
{
    public class DrawOnHandle
    {
        private IntPtr _screenHandle;

        public DrawOnHandle(IntPtr screenHandle)
        {
            _screenHandle = screenHandle;
        }

        public async void DrawBiBaBuLogoAnimation()
        {
            Image bibabuImage = new Bitmap(Properties.Resources.LogoBiBaBuColoring);
            Graphics screenGrahpics = Graphics.FromHwnd(_screenHandle);
            var screenSize = screenGrahpics.VisibleClipBounds;

            Bitmap doubleBuffer = new Bitmap((int)screenSize.Width, (int)screenSize.Height);
            Graphics gBuffer = Graphics.FromImage(doubleBuffer);
            
            for (int x = 100; x < (screenSize.Width - 1100); x += 3)
            {
                //screenGrahpics.DrawImage(bibabuImage, x, 100, 1000, 1000);
                gBuffer.Clear(Color.Gray);
                gBuffer.DrawImage(bibabuImage, x, 100, 1000, 1000);
                gBuffer.Flush();

                screenGrahpics.DrawImageUnscaled(doubleBuffer, 0, 0);

                await Task.Delay(16);
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
    }
}
