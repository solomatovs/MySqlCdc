using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using MySql.Cdc.Constants;
using MySql.Cdc.Protocol;

namespace MySql.Cdc.Network
{
    public class DatabaseConnection
    {
        private readonly ConnectionOptions _options;
        private readonly Socket _socket;

        public Stream Stream { get; private set; }

        public DatabaseConnection(ConnectionOptions options)
        {
            _options = options;

            _socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            _socket.Connect(new IPEndPoint(IPAddress.Loopback, _options.Port));
            Stream = new NetworkStream(_socket);
        }

        public async Task WriteBytesAsync(byte[] array)
        {
            await Stream.WriteAsync(array, 0, array.Length);
        }

        public async Task WriteCommandAsync(ICommand command, byte sequenceNumber)
        {
            var array = command.CreatePacket(sequenceNumber);
            await Stream.WriteAsync(array, 0, array.Length);
        }

        /// <summary>
        /// In sequential mode packet type is determined by calling client code.
        /// We don't use System.IO.Pipelines as it cannot determine packet type.
        /// </summary>
        public async Task<byte[]> ReadPacketSlowAsync()
        {
            byte[] header = new byte[PacketConstants.HeaderSize];
            await Stream.ReadAsync(header, 0, header.Length);

            // We don't care about packet splitting in handshake flow
            var bodySize = header[0] + (header[1] << 8) + (header[2] << 16);
            byte[] body = new byte[bodySize];
            await Stream.ReadAsync(body, 0, body.Length);

            return body;
        }

        public void UpgradeToSsl()
        {
            var sslStream = new SslStream(Stream, false, new RemoteCertificateValidationCallback(ValidateServerCertificate), null);
            sslStream.AuthenticateAsClient(_options.Hostname);
            Stream = sslStream;
        }

        private bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            if (sslPolicyErrors == SslPolicyErrors.None)
                return true;

            return false;
        }
    }
}
