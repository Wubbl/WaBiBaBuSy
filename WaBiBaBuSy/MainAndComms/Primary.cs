using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace WaBiBaBuSy
{
    internal class Primary
    {
        private TcpListener? _server;

        public async void StartPrimary()
        {
            IPAddress localIP = IPAddress.Any;

            _server = new TcpListener(localIP, MainLoop.Port);

            // Start listening for client requests.
            _server.Start();
            Debug.WriteLine("Server started!");

            await Listening();
        }

        private async Task Listening()
        {
            while (true)
            {
                if (_server != null)
                {
                    var client = await _server.AcceptTcpClientAsync().ConfigureAwait(false);
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
    }
}
