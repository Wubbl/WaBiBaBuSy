using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using WaBiBaBuSy.Wallpaper;

namespace WaBiBaBuSy
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        readonly DrawOnHandle _drawOn;

        public MainWindow()
        {
            InitializeComponent();

            // Find Handle
            IntPtr screenHandle = WindowHandle.FindWindowHandle();

            if (screenHandle != IntPtr.Zero)
            {
                _drawOn = new DrawOnHandle(screenHandle);
            }
        }

        private void DrawImage_Click(object sender, RoutedEventArgs e)
        {
            if (_drawOn != null)
            {
                _drawOn.DrawBiBaBuLogo();
            }
        }

        private void ResetWallpaper_Click(object sender, RoutedEventArgs e)
        {
            if (_drawOn != null)
            {
                _drawOn.ClearWallpaper();
            }
        }
    }
}
