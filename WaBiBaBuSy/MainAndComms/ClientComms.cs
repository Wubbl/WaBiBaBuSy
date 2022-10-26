using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace WaBiBaBuSy
{
    internal class ClientComms
    {
        private UdpClient _udpClient;
        private CancellationTokenSource _cts;
        private IPAddress _knownServer;

        public async void StartClient()
        {
            Debug.WriteLine("Client started!");

            _udpClient = new UdpClient(MainLoop.Port);
            _udpClient.EnableBroadcast = true;

            _cts = new CancellationTokenSource();
            await ReceiveBroadcasts();
        }

        public void StopClient()
        {
            _cts.Cancel();
            _udpClient?.Close();
        }

        public void ConnectToServer()
        {
            try
            {
                Classes.UDPPackage hello = new Classes.UDPPackage(Classes.PayloadType.ClientHello, System.Text.Encoding.ASCII.GetBytes(_knownServer.ToString() + "_" + Dns.GetHostName()));
                
                // UDP Package
                //IPEndPoint endPoint = new IPEndPoint(MainLoop.IPcontrolServer, MainLoop.Port);
                //_udpClient.SendAsync(hello.GetBytes(), hello.Length(), endPoint);
                //Debug.WriteLine("Client: Sent hello message to Server");

                // Prefer using declaration to ensure the instance is Disposed later.
                using TcpClient client = new TcpClient(MainLoop.IPcurrent.ToString(), MainLoop.Port);

                // Get a client stream for reading and writing.
                NetworkStream stream = client.GetStream();

                // Send the message to the connected TcpServer.
                stream.Write(hello.GetBytes(), 0, hello.Length());
                Debug.WriteLine("Client: Sent hello message to Server");

                // Receive the server response.
                // Buffer to store the response bytes.
                byte[] data = new byte[256];

                // Read the first batch of the TcpServer response bytes.
                int bytes = stream.Read(data, 0, data.Length);

                string responseData = System.Text.Encoding.ASCII.GetString(data, 0, bytes);
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

        public async Task ReceiveBroadcasts()
        {
            try
            {
                var receiveResult = await _udpClient.ReceiveAsync(_cts.Token);
                string serverIP = Encoding.ASCII.GetString(receiveResult.Buffer);

                Debug.WriteLine("From {0} received: {1} ", receiveResult.RemoteEndPoint.Address.ToString(), serverIP);

                MainLoop.IPcontrolServer = receiveResult.RemoteEndPoint.Address;

                if (_knownServer == null)
                {
                    _knownServer = receiveResult.RemoteEndPoint.Address;
                    ConnectToServer();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Client: Exception in BroadcastReceived: " + ex.Message);
            }
        }
    }
}
