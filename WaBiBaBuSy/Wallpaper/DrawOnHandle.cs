using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
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
            Graphics screenGrahpics = Graphics.FromHwnd(_screenHandle);

            screenGrahpics.Clear(Color.Transparent);
        }
    }
}
