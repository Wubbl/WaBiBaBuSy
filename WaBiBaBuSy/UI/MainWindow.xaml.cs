using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using WaBiBaBuSy.Wallpaper;

namespace WaBiBaBuSy
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private MainLoop _mainLoop;

        public MainWindow(MainLoop refToMain)
        {
            InitializeComponent();

            _mainLoop = refToMain;
        }

        private void btn_ResetWallpaper_Click(object sender, RoutedEventArgs e)
        {

        }

        private void btn_DrawImage_Click(object sender, RoutedEventArgs e)
        {
            //if (_drawOn != null)
            //{
            //    _drawOn.DrawBiBaBuLogo();
            //}
        }

        private void btn_StartServer_Click(object sender, RoutedEventArgs e)
        {

        }

        private void btn_Connect_Click(object sender, RoutedEventArgs e)
        {
            
        }   
    }
}
