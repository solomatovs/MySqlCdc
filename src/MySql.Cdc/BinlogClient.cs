using System;
using System.Buffers;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MySql.Cdc.Checksum;
using MySql.Cdc.Commands;
using MySql.Cdc.Constants;
using MySql.Cdc.Events;
using MySql.Cdc.Network;
using MySql.Cdc.Packets;
using MySql.Cdc.Protocol;
using MySql.Cdc.Providers;

namespace MySql.Cdc
{
    /// <summary>
    /// MySql replication client streaming binlog events in real-time.
    /// </summary>
    public class BinlogClient
    {
        private readonly List<string> _allowedAuthPlugins = new List<string>
        {
            AuthPluginNames.MySqlNativePassword,
            AuthPluginNames.CachingSha2Password
        };
        private readonly ConnectionOptions _options = new ConnectionOptions();
        private IDatabaseProvider _databaseProvider;
        private DatabaseConnection _channel;

        public BinlogClient(Action<ConnectionOptions> configureOptions)
        {
            configureOptions(_options);
        }

        private async Task ConnectAsync()
        {
            _channel = new DatabaseConnection(_options);

            var packet = await _channel.ReadPacketSlowAsync();
            ThrowIfErrorPacket(packet, "Initial handshake error.");
            var handshake = new HandshakePacket(new ReadOnlySequence<byte>(packet));

            if (!_allowedAuthPlugins.Contains(handshake.AuthPluginName))
                throw new InvalidOperationException($"Authentication plugin {handshake.AuthPluginName} is not supported.");

            _databaseProvider = handshake.ServerVersion.IndexOf("MariaDB", StringComparison.InvariantCultureIgnoreCase) >= 0
            ? (IDatabaseProvider)new MariaDbProvider()
            : new MySqlProvider();

            await AuthenticateAsync(handshake);
        }

        private async Task AuthenticateAsync(HandshakePacket handshake)
        {
            byte sequenceNumber = 1;

            if (_options.UseSsl)
            {
                var command = new SslRequestCommand(handshake.ServerCollation);
                await _channel.WriteCommandAsync(command, sequenceNumber);
                sequenceNumber += 1;
                _channel.UpgradeToSsl();
            }

            var authenticateCommand = new AuthenticateCommand(_options, handshake.ServerCollation, handshake.Scramble, handshake.AuthPluginName);
            await _channel.WriteCommandAsync(authenticateCommand, sequenceNumber);
            var packet = await _channel.ReadPacketSlowAsync();
            sequenceNumber += 2;

            ThrowIfErrorPacket(packet, "Authentication error.");

            if (packet[0] == (byte)ResponseType.Ok)
                return;

            if (packet[0] == (byte)ResponseType.AuthPluginSwitch)
            {
                var body = new ReadOnlySequence<byte>(packet, 1, packet.Length - 1);
                var switchRequest = new AuthPluginSwitchPacket(body);
                await HandleAuthPluginSwitch(switchRequest, sequenceNumber);
            }
            else
            {
                await AuthenticateSha256Async(packet, handshake.Scramble, sequenceNumber);
            }
        }

        private async Task HandleAuthPluginSwitch(AuthPluginSwitchPacket switchRequest, byte sequenceNumber)
        {
            if (!_allowedAuthPlugins.Contains(switchRequest.AuthPluginName))
                throw new InvalidOperationException($"Authentication plugin {switchRequest.AuthPluginName} is not supported.");

            var switchCommand = new AuthPluginSwitchCommand(_options.Password, switchRequest.AuthPluginData, switchRequest.AuthPluginName);
            await _channel.WriteCommandAsync(switchCommand, sequenceNumber);
            var packet = await _channel.ReadPacketSlowAsync();
            sequenceNumber += 2;
            ThrowIfErrorPacket(packet, "Authentication switch error.");

            if (switchRequest.AuthPluginName == AuthPluginNames.CachingSha2Password)
            {
                await AuthenticateSha256Async(packet, switchRequest.AuthPluginData, sequenceNumber);
            }
        }

        private async Task AuthenticateSha256Async(byte[] packet, string scramble, byte sequenceNumber)
        {
            // See https://mariadb.com/kb/en/caching_sha2_password-authentication-plugin/
            // Success authentication.
            if (packet[0] == 0x01 && packet[1] == 0x03)
                return;

            // Send clear password if ssl is used.
            var writer = new PacketWriter(sequenceNumber);
            if (_options.UseSsl)
            {
                writer.WriteNullTerminatedString(_options.Password);
                await _channel.WriteBytesAsync(writer.CreatePacket());
                packet = await _channel.ReadPacketSlowAsync();
                ThrowIfErrorPacket(packet, "Sending caching_sha2_password clear password error.");
                return;
            }

            // Request public key.
            writer = new PacketWriter(sequenceNumber);
            writer.WriteByte(0x02);
            await _channel.WriteBytesAsync(writer.CreatePacket());
            packet = await _channel.ReadPacketSlowAsync();
            sequenceNumber += 2;
            ThrowIfErrorPacket(packet, "Requesting caching_sha2_password public key.");

            // Extract public key.
            var publicKeyString = Encoding.UTF8.GetString(new ReadOnlySequence<byte>(packet, 1, packet.Length - 1).ToArray());
            publicKeyString = publicKeyString
                .Replace("-----BEGIN PUBLIC KEY-----", "")
                .Replace("-----END PUBLIC KEY-----", "")
                .Replace("\n", "");
            byte[] publicKey = Convert.FromBase64String(publicKeyString);

            // Password must be null terminated. Not documented in MariaDB.
            var password = Encoding.UTF8.GetBytes(_options.Password += '\0');
            var encryptedPassword = AuthenticateCommand.Xor(password, Encoding.UTF8.GetBytes(scramble));

            using (var rsa = RSA.Create())
            {
                rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
                var encryptedBody = rsa.Encrypt(encryptedPassword, RSAEncryptionPadding.OaepSHA1);

                writer = new PacketWriter(sequenceNumber);
                writer.WriteByteArray(encryptedBody);
                await _channel.WriteBytesAsync(writer.CreatePacket());
                packet = await _channel.ReadPacketSlowAsync();
                ThrowIfErrorPacket(packet, "Authentication error.");
            }
        }

        public async Task ReplicateAsync(Func<IBinlogEvent, Task> handleEvent)
        {
            await ConnectAsync();

            await AdjustStartingPosition();
            await SetMasterHeartbeat();
            await SetMasterBinlogChecksum();
            await _databaseProvider.DumpBinlogAsync(_channel, _options);
            await ReadEventStreamAsync(handleEvent);
        }

        private async Task ReadEventStreamAsync(Func<IBinlogEvent, Task> handleEvent)
        {
            var channel = new EventStreamChannel(_databaseProvider.Deserializer, _channel.Stream);
            while (true)
            {
                var timeout = _options.HeartbeatInterval.Add(TimeSpan.FromMilliseconds(TimeoutConstants.Delta));
                var packet = await channel.ReadPacketAsync().WithTimeout(timeout, TimeoutConstants.Message);

                if (packet is IBinlogEvent binlogEvent)
                {
                    // We stop replication if client code throws an exception 
                    // As a derived database may end up in an inconsistent state.
                    await handleEvent(binlogEvent);
                    UpdateBinlogPosition(binlogEvent);
                }

                else if (packet is ErrorPacket error)
                    throw new InvalidOperationException($"Event stream error. {error.ToString()}");

                else if (packet is EndOfFilePacket && !_options.Blocking)
                    return;

                else throw new InvalidOperationException($"Event stream unexpected error.");
            }
        }

        private void UpdateBinlogPosition(IBinlogEvent binlogEvent)
        {
            // Rows event depends on preceding TableMapEvent & we change the position
            // after we read them atomically to prevent missing mapping on reconnect.
            // Figure out something better as TableMapEvent can be followed by several row events.
            if (binlogEvent is TableMapEvent tableMapEvent)
                return;

            if (binlogEvent is RotateEvent rotateEvent)
            {
                _options.Binlog.Filename = rotateEvent.BinlogFilename;
                _options.Binlog.Position = rotateEvent.BinlogPosition;
            }
            else if (binlogEvent.Header.NextEventPosition > 0)
            {
                _options.Binlog.Position = binlogEvent.Header.NextEventPosition;
            }
        }

        private async Task AdjustStartingPosition()
        {
            if (_options.Binlog.StartingStrategy != StartingStrategy.FromEnd)
                return;

            // Ignore if position was read before in case of reconnect.
            if (_options.Binlog.Filename != null)
                return;

            var command = new QueryCommand("show master status");
            await _channel.WriteCommandAsync(command, 0);

            var resultSet = await ReadResultSet();
            if (resultSet.Count != 1)
                throw new InvalidOperationException("Could not read master binlog position.");

            _options.Binlog.Filename = resultSet[0].Cells[0];
            _options.Binlog.Position = long.Parse(resultSet[0].Cells[1]);
        }

        private async Task SetMasterHeartbeat()
        {
            var seconds = (long)_options.HeartbeatInterval.TotalSeconds;
            var command = new QueryCommand($"set @master_heartbeat_period={seconds * 1000 * 1000 * 1000}");
            await _channel.WriteCommandAsync(command, 0);
            var packet = await _channel.ReadPacketSlowAsync();
            ThrowIfErrorPacket(packet, "Setting master_binlog_checksum error.");
        }

        private async Task SetMasterBinlogChecksum()
        {
            var command = new QueryCommand("SET @master_binlog_checksum= @@global.binlog_checksum");
            await _channel.WriteCommandAsync(command, 0);
            var packet = await _channel.ReadPacketSlowAsync();
            ThrowIfErrorPacket(packet, "Setting master_binlog_checksum error.");

            command = new QueryCommand($"SELECT @master_binlog_checksum");
            await _channel.WriteCommandAsync(command, 0);
            var resultSet = await ReadResultSet();

            // When replication is started fake RotateEvent comes before FormatDescriptionEvent.
            // In order to deserialize the event we have to obtain checksum type length in advance.
            var checksumType = (ChecksumType)Enum.Parse(typeof(ChecksumType), resultSet[0].Cells[0]);
            _databaseProvider.Deserializer.ChecksumStrategy = checksumType switch
            {
                ChecksumType.None => new NoneChecksum(),
                ChecksumType.CRC32 => new Crc32Checksum(),
                _ => throw new InvalidOperationException("The master checksum type is not supported.")
            };
        }

        private async Task<List<ResultSetRowPacket>> ReadResultSet()
        {
            var packet = await _channel.ReadPacketSlowAsync();
            ThrowIfErrorPacket(packet, "Reading result set error.");

            while (true)
            {
                // Skip through metadata
                packet = await _channel.ReadPacketSlowAsync();
                if (packet[0] == (byte)ResponseType.EndOfFile)
                    break;
            }

            var resultSet = new List<ResultSetRowPacket>();
            while (true)
            {
                packet = await _channel.ReadPacketSlowAsync();
                ThrowIfErrorPacket(packet, "Query result set error.");

                if (packet[0] == (byte)ResponseType.EndOfFile)
                    break;

                resultSet.Add(new ResultSetRowPacket(new ReadOnlySequence<byte>(packet)));
            }
            return resultSet;
        }

        private void ThrowIfErrorPacket(byte[] packet, string message)
        {
            if (packet[0] == (byte)ResponseType.Error)
            {
                var error = new ErrorPacket(new ReadOnlySequence<byte>(packet, 1, packet.Length - 1));
                throw new InvalidOperationException($"{message} {error.ToString()}");
            }
        }
    }
}
