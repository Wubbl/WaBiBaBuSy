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
        private readonly DrawOnHandle? _drawOn = null;

        private List<string> _clients;
        private List<TcpClient> _tcpClients;

        private TcpListener? _server;

        public MainWindow()
        {
            InitializeComponent();

            // Find Handle
            IntPtr screenHandle = WindowHandle.FindWindowHandle();

            _clients = new List<string>();
            _tcpClients = new List<TcpClient>();

            listBox_Clients.ItemsSource = _clients;

            var host = Dns.GetHostEntry(Dns.GetHostName());

            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    textBox_IP.Text = ip.ToString();
                }
            }

            if (screenHandle != IntPtr.Zero)
            {
                _drawOn = new DrawOnHandle(screenHandle);
            }
        }

        private void btn_ResetWallpaper_Click(object sender, RoutedEventArgs e)
        {
            if (_drawOn != null)
            {
                _drawOn.ClearWallpaper();
            }
        }

        private void btn_DrawImage_Click(object sender, RoutedEventArgs e)
        {
            if (_drawOn != null)
            {
                _drawOn.DrawBiBaBuLogo();
            }
        }

        private async void btn_StartServer_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(textBox_Port.Text, out int port))
            {
                IPAddress localIP = IPAddress.Any;

                _server = new TcpListener(localIP, port);

                // Start listening for client requests.
                _server.Start();
                Debug.WriteLine("Server started!");

                await Listening();
            }
        }

        private async Task Listening()
        {
            while (true)
            {
                if (_server != null)
                {
                    var client = await _server.AcceptTcpClientAsync().ConfigureAwait(false);
                    Debug.WriteLine("Client connected!");

                    if (client != null && client.Client.RemoteEndPoint != null) _clients.Add(((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString());

                    await Task.Run(() => RetrieveFirstMessageFromClient(client));
                }
            }
        }

        public async Task RetrieveFirstMessageFromClient(TcpClient? client)
        {
            // Buffer for reading data
            Byte[] bytes = new Byte[256];
            string data = null;
            int i;

            try
            {
                using (var stream = client.GetStream())
                {
                    // Loop to receive all the data sent by the client.
                    while ((i = stream.Read(bytes, 0, bytes.Length)) != 0)
                    {
                        // Translate data bytes to a ASCII string.
                        data = System.Text.Encoding.ASCII.GetString(bytes, 0, i);
                        Debug.WriteLine("Received: " + data);

                        // Process the data sent by the client.
                        data = data.ToUpper();

                        byte[] msg = System.Text.Encoding.ASCII.GetBytes(data);

                        // Send back a response.
                        stream.Write(msg, 0, msg.Length);
                        Debug.WriteLine("Sent: " + data);
                    }

                    _drawOn?.DrawBiBaBuLogo();

                    // Shutdown and end the connection
                    Debug.WriteLine("Client closing");
                    client.Close();
                }
            }
            finally
            {
                if (client != null)
                {
                    (client as IDisposable).Dispose();
                }
            }
        }

        private void btn_Connect_Click(object sender, RoutedEventArgs e)
        {
            string message = "Hello";

            if (int.TryParse(textBox_Port.Text, out int port) && IPAddress.TryParse(textBox_IP.Text, out IPAddress? ip))
            {
                try
                {
                    // Prefer using declaration to ensure the instance is Disposed later.
                    using TcpClient client = new TcpClient(ip.ToString(), port);

                    // Translate the passed message into ASCII and store it as a Byte array.
                    Byte[] data = System.Text.Encoding.ASCII.GetBytes(message);

                    // Get a client stream for reading and writing.
                    NetworkStream stream = client.GetStream();

                    // Send the message to the connected TcpServer.
                    stream.Write(data, 0, data.Length);

                    Debug.WriteLine("Sent: " + message);

                    // Receive the server response.

                    // Buffer to store the response bytes.
                    data = new Byte[256];

                    // String to store the response ASCII representation.
                    String responseData = String.Empty;

                    // Read the first batch of the TcpServer response bytes.
                    Int32 bytes = stream.Read(data, 0, data.Length);
                    responseData = System.Text.Encoding.ASCII.GetString(data, 0, bytes);
                    Debug.WriteLine("Received: " + responseData);

                    // Explicit close is not necessary since TcpClient.Dispose() will be
                    // called automatically.
                    // stream.Close();
                    // client.Close();
                }
                catch (ArgumentNullException ex)
                {
                    Debug.WriteLine("ArgumentNullException: " + ex);
                }
                catch (SocketException ex)
                {
                    Debug.WriteLine("SocketException: " + ex);
                }
            }
        }   
    }
}
