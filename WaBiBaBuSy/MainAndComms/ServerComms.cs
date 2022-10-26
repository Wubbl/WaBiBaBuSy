using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WaBiBaBuSy
{
    internal class ServerComms
    {
        private TcpListener _tcpReceiver;
        private UdpClient _udpClient;

        private System.Timers.Timer _timerBroadcast;
        private int _broadcastsLeft = 0;

        public async void StartServer()
        {
            IPAddress localIP = IPAddress.Any;

            _tcpReceiver = new TcpListener(localIP, MainLoop.Port);

            // Start listening for client requests.
            _tcpReceiver.Start();

            Debug.WriteLine("Server started!");

            await ListeningForTCPRequests();
        }

        public void StopServer()
        {

        }

        private async Task ListeningForTCPRequests()
        {
            while (true)
            {
                if (_tcpReceiver != null)
                {
                    var client = await _tcpReceiver.AcceptTcpClientAsync().ConfigureAwait(false);
                    Debug.WriteLine("Client connected!");

                    //if (client != null && client.Client.RemoteEndPoint != null) _clients.Add(((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString());

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

        public void StartBroadcast(int durationInSec)
        {
            _udpClient = new UdpClient();
            _udpClient.EnableBroadcast = true;

            _broadcastsLeft = (durationInSec) / 5;

            _timerBroadcast = new System.Timers.Timer(5000);

            _timerBroadcast.Elapsed += TimerBroadcast_Elapsed;
            _timerBroadcast.AutoReset = true;
            _timerBroadcast.Enabled = true;

            UDPBroadcast(MainLoop.Port);
        }

        private void TimerBroadcast_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            UDPBroadcast(MainLoop.Port);

            if (--_broadcastsLeft == 0)
            {
                _timerBroadcast.Enabled = false;
                _udpClient.Close();
            }
        }

        public void UDPBroadcast(int port)
        {
            //var firstTwoOctetSegments = string.Join(".", MainLoop.IPcurrent.GetAddressBytes()[0], MainLoop.IPcurrent.GetAddressBytes()[1]);

            byte[] bytes = Encoding.ASCII.GetBytes(MainLoop.IPcurrent.ToString());

            IPEndPoint endPoint = new IPEndPoint(IPAddress.Broadcast, MainLoop.Port);
            _udpClient.SendAsync(bytes, bytes.Length, endPoint);

            Debug.WriteLine("Primary: Broadcast sent with payload: " + MainLoop.IPcurrent.ToString());
        }
    }
}
