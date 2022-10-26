using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WaBiBaBuSy
{
    public static class Classes
    {
        public enum PayloadType
        {
            ClientHello
        }

        public class UDPPackage
        {
            public PayloadType Type { get; set; }

            public byte[] Payload { get; set; }

            public UDPPackage(byte[] bytes)
            {
                Type = (PayloadType)BitConverter.ToInt32(bytes, 0);
                Payload = bytes.Skip(4).ToArray();
            }

            public UDPPackage(PayloadType type, byte[] payload)
            {
                Type = type;
                Payload = payload;
            }

            public int Length()
            {
                return 4 + Payload.Length;
            }

            public byte[] GetBytes()
            {
                // INT 4 byte + Payload
                byte[] bytes = new byte[4 + Payload.Length];

                Array.Copy(BitConverter.GetBytes((int)Type), 0, bytes, 0, 4);
                Array.Copy(Payload, 0, bytes, 4, Payload.Length);

                return bytes;
            }
        }

    }
}
