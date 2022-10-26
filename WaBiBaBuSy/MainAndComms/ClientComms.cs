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
        private bool _stop = false;

        public async void StartClient()
        {
            Debug.WriteLine("Client started!");

            await ReceiveBroadcasts();
        }

        public void StopClient()
        {
            _stop = true;
            _udpClient?.Close();
            _udpClient?.Dispose();
        }

        public void Connect()
        {
            string message = "Hello";

            try
            {
                // Prefer using declaration to ensure the instance is Disposed later.
                using TcpClient client = new TcpClient(MainLoop.ipCurrent.ToString(), MainLoop.Port);

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

        public async Task ReceiveBroadcasts()
        {
            using (_udpClient = new UdpClient(MainLoop.Port) { EnableBroadcast = true })
            {
                while (!_stop)
                {
                    try
                    {
                        IPEndPoint endPoint = new IPEndPoint(IPAddress.Any, MainLoop.Port);

                        CancellationTokenSource cts = new CancellationTokenSource();
                        cts.CancelAfter(10000);

                        var receiveResult = await _udpClient.ReceiveAsync(cts.Token);
                        string serverIP = Encoding.ASCII.GetString(receiveResult.Buffer);

                        Debug.WriteLine("From {0} received: {1} ", endPoint.Address.ToString(), serverIP);

                        MainLoop.ipControlServer = endPoint.Address;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Client: Exception in BroadcastReceived: " + ex.Message);
                    }
                }
            }
        }
    }
}
