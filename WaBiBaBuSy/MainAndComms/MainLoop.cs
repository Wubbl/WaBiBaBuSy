using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using WaBiBaBuSy.Wallpaper;

namespace WaBiBaBuSy
{
    public class MainLoop
    {
        public static int Port { get; set; } = 51234;
        public static IPAddress ipCurrent { get; set; } = IPAddress.Loopback;
        public static IPAddress ipControlServer 
        { 
            get => _ipControlServer; 
            set
            {
                _ipControlServer = value;
                _mainUI?.UpdateTitle("Client connected to: " + ipControlServer.ToString());
            }
         }

        private static IPAddress _ipControlServer = IPAddress.Loopback;
        private static MainWindow _mainUI;

        private ServerOrClientMode _mode;
        private DrawOnHandle? _drawOn = null;
        
        private List<string> _clients;
        private List<TcpClient> _tcpClients;

        
        private ServerComms _serverComms;
        private ClientComms _clientComms;   

        public ServerOrClientMode Mode
        {
            get => _mode;
            set
            {
                _mode = value;
                if (_mode == ServerOrClientMode.Server)
                {
                    _clientComms?.StopClient();
                    _clientComms = null;

                    if (_serverComms == null)
                    {
                        _serverComms = new ServerComms();
                        _serverComms.StartServer();
                    }
                }
                else
                {
                    _serverComms?.StopServer();
                    _serverComms = null;

                    if (_clientComms == null)
                    {
                        _clientComms = new ClientComms();
                        _clientComms.StartClient();
                    }
                }
            }
        }

        public enum ServerOrClientMode
        {
            Client,
            Server
        }

        public MainLoop()
        {
            // Find Handle
            IntPtr screenHandle = WindowHandle.FindWindowHandle();

            Mode = ServerOrClientMode.Client;

            _clients = new List<string>();
            _tcpClients = new List<TcpClient>();

            var host = Dns.GetHostEntry(Dns.GetHostName());

            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    ipCurrent = ip;
                    break;
                }
            }

            if (screenHandle != IntPtr.Zero)
            {
                _drawOn = new DrawOnHandle(screenHandle);
            }
        }

        public void ShowUI()
        {
            if (_mainUI == null) _mainUI = new MainWindow(this);
            _mainUI.Show();
        }

        public async void LoopMain()
        {

        }

        #region Public Methods for UI

        public void CleartWallpaper()
        {
            _drawOn?.ClearWallpaper();
        }

        public async void DrawBiBaBuLogAnimation()
        {
            await Task.Run(() => _drawOn?.DrawBiBaBuLogoAnimation());
            //await Task.Run(() => _drawOn?.DrawDirect2DBiBaBuLogoAnimation());
        }

        public void StartBroadcast(int durationInSec)
        {
            Debug.WriteLine("Broadcast started for " + durationInSec + " seconds!");
            _serverComms?.StartBroadcast(durationInSec);
        }

        #endregion Public Methods for UI

    }
}
