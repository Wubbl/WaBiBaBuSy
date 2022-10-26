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
        private CancellationTokenSource _cts;

        private System.Timers.Timer _timerBroadcast;
        private int _broadcastsLeft = 0;

        public async void StartServer()
        {
            IPAddress localIP = IPAddress.Any;

            _tcpReceiver = new TcpListener(localIP, MainLoop.Port);

            // Start listening for client requests.
            _tcpReceiver.Start();

            Debug.WriteLine("Server started!");

            _cts = new CancellationTokenSource();

            _udpClient = new UdpClient(MainLoop.Port);
            _udpClient.EnableBroadcast = true;

            await ReceiveClientPackages();
        }

        public void StopServer()
        {
            _cts.Cancel();
            _udpClient?.Close();
        }
        private async Task ListeningForTCPRequests()
        {
            while (true)
            {
                if (_tcpReceiver != null)
                {
                    var client = await _tcpReceiver.AcceptTcpClientAsync().ConfigureAwait(false);
                    Debug.WriteLine("Client Hello messag received from: " + ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString());

                    //if (client != null && client.Client.RemoteEndPoint != null) _clients.Add(((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString());

                    await Task.Run(() => RetrieveFirstMessageFromClient(client));
                }
            }
        }

        public async Task RetrieveFirstMessageFromClient(TcpClient? client)
        {
            // Buffer for reading data
            byte[] bytes = new byte[256];
            int i;

            try
            {
                using (var stream = client.GetStream())
                {
                    // Loop to receive all the data sent by the client.
                    while ((i = stream.Read(bytes, 0, bytes.Length)) != 0)
                    {
                        Classes.UDPPackage data = new Classes.UDPPackage(bytes);

                        Debug.WriteLine("Server: Received: " + data.Type);

                        byte[] msg = System.Text.Encoding.ASCII.GetBytes("ACK");

                        // Send back a response.
                        stream.Write(msg, 0, msg.Length);
                        Debug.WriteLine("Server: Sent: " + data);
                    }

                    // Shutdown and end the connection
                    Debug.WriteLine("Server: TCPClient closing");
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

        public async Task ReceiveClientUDPPackages()
        {
            try
            {
                var receiveResult = await _udpClient.ReceiveAsync(_cts.Token);
                Classes.UDPPackage data = new Classes.UDPPackage(receiveResult.Buffer);

                Debug.WriteLine("From {0} received: {1} ", receiveResult.RemoteEndPoint.Address.ToString(), data.Type.ToString());

                switch (data.Type)
                {
                    case Classes.PayloadType.ClientHello:

                        break;
                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Server: Exception in ReceiveClientPackages: " + ex.Message);
            }
        }
    }
}
