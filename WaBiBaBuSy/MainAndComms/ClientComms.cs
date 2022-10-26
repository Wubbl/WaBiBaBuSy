using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace WaBiBaBuSy
{
    internal class ClientComms
    {
        private UdpClient _udpClient;

        public void StartClient()
        {
            _udpClient = new UdpClient(MainLoop.Port) { EnableBroadcast = true };

            _udpClient.BeginReceive(BroadcastReceived, null);

            Debug.WriteLine("Client started!");
        }

        public void StopClient()
        {

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

        public void BroadcastReceived(IAsyncResult ar)
        {
            IPEndPoint endPoint = new IPEndPoint(IPAddress.Any, MainLoop.Port);

            byte[] bytes = _udpClient.EndReceive(ar, ref endPoint);
            string serverIP = Encoding.ASCII.GetString(bytes);

            Debug.WriteLine("From {0} received: {1} ", endPoint.Address.ToString(), serverIP);

            MainLoop.ipControlServer = endPoint.Address;

            _udpClient.BeginReceive(BroadcastReceived, null);
        }
    }
}
