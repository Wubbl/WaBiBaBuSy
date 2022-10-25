using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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

        public void DrawBiBaBuLogo()
        {
            Image bibabuImage = new Bitmap(Properties.Resources.LogoBiBaBuColoring);

            Graphics screenGrahpics = Graphics.FromHwnd(_screenHandle);

            screenGrahpics.DrawImage(bibabuImage, 100, 100, 1000, 1000);

            Debug.WriteLine(screenGrahpics.VisibleClipBounds);
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
