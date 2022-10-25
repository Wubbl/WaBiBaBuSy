using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using WaBiBaBuSy.Wallpaper;

namespace WaBiBaBuSy
{
    public class MainLoop
    {
        private readonly DrawOnHandle? _drawOn = null;

        private List<string> _clients;
        private List<TcpClient> _tcpClients;

        

        public static int Port = 51234;
        public static IPAddress PrimeIP = IPAddress.Loopback;

        public MainLoop()
        {
            // Find Handle
            IntPtr screenHandle = WindowHandle.FindWindowHandle();

            _clients = new List<string>();
            _tcpClients = new List<TcpClient>();

            var host = Dns.GetHostEntry(Dns.GetHostName());

            //foreach (var ip in host.AddressList)
            //{
            //    if (ip.AddressFamily == AddressFamily.InterNetwork)
            //    {
            //        textBox_IP.Text = ip.ToString();
            //    }
            //}

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
        }

        #endregion Public Methods for UI

    }
}
