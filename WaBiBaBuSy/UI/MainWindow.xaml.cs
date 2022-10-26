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

            UpdateTitle("Client");

            _mainLoop = refToMain;
        }

        private void btn_ResetWallpaper_Click(object sender, RoutedEventArgs e)
        {
            _mainLoop.CleartWallpaper();
        }

        private void btn_DrawImage_Click(object sender, RoutedEventArgs e)
        {
            _mainLoop.DrawBiBaBuLogAnimation();
        }

        private void btn_StartServer_Click(object sender, RoutedEventArgs e)
        {

        }

        private void btn_Connect_Click(object sender, RoutedEventArgs e)
        {
            _mainLoop.ConnectFromClient();
        }

        private void btn_SwitchMode_Click(object sender, RoutedEventArgs e)
        {
            if (_mainLoop.Mode == MainLoop.ServerOrClientMode.Client)
            {
                _mainLoop.Mode = MainLoop.ServerOrClientMode.Server;
                UpdateTitle("Server");
            }
            else
            {
                _mainLoop.Mode = MainLoop.ServerOrClientMode.Client;
                UpdateTitle("Client");
            }
        }

        public void UpdateTitle(string titleAddition)
        {
            this.Title = "Wallpaper Bier Bart und Busen Synchromat - " + titleAddition;
        }

        private void btn_StartBroadcast_Click(object sender, RoutedEventArgs e)
        {
            _mainLoop.StartBroadcast((int)slider_BroadcastDuration.Value);
        }
    }
}
