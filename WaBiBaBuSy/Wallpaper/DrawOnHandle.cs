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
            Image bibabuImage = Image.FromFile("C:\\Users\\Patrick\\Dropbox\\Wohnung\\BiBaBu LOGO\\Sticker\\LogoBiBaBuColoring.png");

            Graphics screenGrahpics = Graphics.FromHwnd(_screenHandle);

            screenGrahpics.DrawImage(bibabuImage, 100, 100);

            Debug.WriteLine(screenGrahpics.VisibleClipBounds);
        }

        public void ClearWallpaper()
        {
            Graphics screenGrahpics = Graphics.FromHwnd(_screenHandle);

            screenGrahpics.Clear(Color.Transparent);
        }
    }
}
