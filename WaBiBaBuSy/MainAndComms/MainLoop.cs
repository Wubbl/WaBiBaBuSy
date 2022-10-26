using System;
using System.Collections.Generic;
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
        public static IPAddress currentIP { get; set; } = IPAddress.Loopback;

        private ServerOrClientMode mode;
        private DrawOnHandle? _drawOn = null;

        private List<string> _clients;
        private List<TcpClient> _tcpClients;

        private ServerComms serverComms;
        private ClientComms clientComms;

        public ServerOrClientMode Mode
        {
            get => mode;
            set
            {
                mode = value;
                if (mode == ServerOrClientMode.Server)
                {
                    serverComms = new ServerComms();
                    clientComms?.StopClient();
                }
                else
                {
                    clientComms = new ClientComms();
                    serverComms?.StopServer();
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
                    currentIP = ip;
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
            MainWindow ui = new MainWindow(this);
            ui.Show();
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

        #endregion Public Methods for UI

    }
}
