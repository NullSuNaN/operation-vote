using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using operation_vote.Client.Request;

namespace operation_vote.Client
{
    public class VotingClient<T> : IDisposable where T : ISocketRequestHandler
    {
        public readonly T SocketRequestHandler;
        private string targetURI;
        private readonly ConcurrentDictionary<long, Operation> _pendingOperations = new();
        private readonly SemaphoreSlim _connectionLock = new(1, 1);

        // Local storage for parsed Operation Types initialized during connection handshake
        private readonly ConcurrentDictionary<long, Operation.OperationType> _operationTypes = new();

        private const int RECONNECT_LIMIT = 3;
        private int _reconnectAttempts;
        private bool _isDisposed;
		public bool Disconnected => _isDisposed;


        // Tracks the multi-step initialization handshake frame asynchronously
        private TaskCompletionSource<bool>? _handshakeCompletionSource;

        public string TargetURI
        {
            get => targetURI;
            set
            {
                targetURI = value;
                _ = ReconnectLoopAsync();
            }
        }

        public readonly CancellationToken CancellationToken;
        public readonly VotingEnv Env = new();
        public ReadOnlyDictionary<long, Operation.OperationType> OperationTypes => _operationTypes.AsReadOnly();

        public event EventHandler<(T, string)>? OnDisconnect;

        public VotingClient(T socketRequestHandler, string uri, CancellationToken token = default)
        {
            SocketRequestHandler = socketRequestHandler;
            targetURI = new string(uri);
            CancellationToken = token;

            SocketRequestHandler.OnDataReceived += HandleIncomingData;
            SocketRequestHandler.OnDisconnected += HandleSocketDisconnection;
        }

        /// <summary>
        /// Explicitly triggers connection and performs the structured initialization sequence:
        /// 1. Sends (bit 1)'INIT'
        /// 2. Validates version protocol strings ('VOTE-1.1')
        /// 3. Registers system-wide operation types matching the runtime payload arrays
        /// </summary>
        public async Task ConnectAsync()
        {
            if (_isDisposed) return;

            await _connectionLock.WaitAsync();
            try
            {
                _handshakeCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                string cleanUri = targetURI.TrimStart('+');
                await SocketRequestHandler.ConnectAsync(cleanUri, CancellationToken);

                // Reset reconnect counts on fresh structural initiation
                _reconnectAttempts = 0;

                // Start inbound listener processing pathway
                _ = SocketRequestHandler.StartListeningAsync(CancellationToken);

                // --- STEP 1: Pack and transmit Bit 1 + 'INIT' Handshake Frame ---
                byte[] initTextBytes = Encoding.UTF8.GetBytes("INIT");
                byte[] initPacket = PackBitStream(startingBit: 1, initTextBytes);
                await SocketRequestHandler.SendAsync(initPacket, CancellationToken);

                // Wait for the server data arrival to execute version checks and structural allocations
                bool initialized = await _handshakeCompletionSource.Task;
                if (!initialized)
                {
                    await SocketRequestHandler.DisconnectAsync(CancellationToken);
                    OnDisconnect?.Invoke(this, (SocketRequestHandler, "Failed to initialize the connection."));
                    Dispose();
                }
            }
            catch
            {
                _handshakeCompletionSource?.TrySetResult(false);
                throw;
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        public async Task SendOperationAsync(Operation operation, CancellationToken token = default)
        {
            if (_isDisposed || operation.IsDisposed) return;

            _pendingOperations[operation.Id] = operation;

            byte[] rawOperationBytes = operation.ToByteArray();
            byte[] fullPacket = PackBitStream(startingBit: 0, rawOperationBytes);

            await SocketRequestHandler.SendAsync(fullPacket, token);
        }

        private async Task ReconnectLoopAsync()
        {
            if (_isDisposed) return;

            await _connectionLock.WaitAsync();
            try
            {
                if (SocketRequestHandler.IsConnected)
                {
                    await SocketRequestHandler.DisconnectAsync(CancellationToken);
                }

                string cleanUri = targetURI.TrimStart('+');
                await SocketRequestHandler.ConnectAsync(cleanUri, CancellationToken);

                _reconnectAttempts = 0;

                // 1. Replay cached non-disposed operations
                foreach (var pair in _pendingOperations)
                {
                    if (pair.Value.IsDisposed)
                    {
                        _pendingOperations.TryRemove(pair.Key, out _);
                        continue;
                    }

                    byte[] rawOpBytes = pair.Value.ToByteArray();
                    byte[] replayPacket = PackBitStream(startingBit: 0, rawOpBytes);

                    await SocketRequestHandler.SendAsync(replayPacket, CancellationToken);
                }

                // 2. Re-send Handshake registration
                byte[] regCmdBytes = Encoding.UTF8.GetBytes("REG");
                byte[] registrationPacket = PackBitStream(startingBit: 1, regCmdBytes);

                await SocketRequestHandler.SendAsync(registrationPacket, CancellationToken);

                _ = SocketRequestHandler.StartListeningAsync(CancellationToken);
            }
            catch
            {
                _reconnectAttempts++;
                if (_reconnectAttempts >= RECONNECT_LIMIT)
                {
                    OnDisconnect?.Invoke(this, (SocketRequestHandler, "Connection is interrupted."));
                    Dispose();
                }
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        private static byte[] PackBitStream(byte startingBit, byte[] data)
        {
            int totalBits = 1 + (data.Length * 8);
            int totalBytes = (totalBits + 7) / 8;
            byte[] output = new byte[totalBytes];

            if (startingBit == 1)
            {
                output[0] |= 0x80;
            }

            for (int i = 0; i < data.Length; i++)
            {
                int bitIndex = 1 + (i * 8);
                int byteOffset = bitIndex / 8;
                int shiftOffset = bitIndex % 8;

                output[byteOffset] |= (byte)(data[i] >> shiftOffset);
                
                if (byteOffset + 1 < output.Length)
                {
                    output[byteOffset + 1] |= (byte)(data[i] << (8 - shiftOffset));
                }
            }

            return output;
        }

        private static (byte Prefix, byte[] Payload) UnpackBitStream(byte[] data)
        {
            if (data.Length == 0) return (0, Array.Empty<byte>());

            byte prefix = (byte)((data[0] & 0x80) >> 7);

            int totalPayloadBits = (data.Length * 8) - 1;
            int targetedBytesCount = totalPayloadBits / 8;
            
            byte[] payload = new byte[targetedBytesCount];

            for (int i = 0; i < payload.Length; i++)
            {
                byte currentPart = (byte)(data[i] << 1);
                byte nextPart = (byte)((data[i + 1] & 0x80) >> 7);
                payload[i] = (byte)(currentPart | nextPart);
            }

            return (prefix, payload);
        }

        private void HandleIncomingData(object? sender, ReadOnlyMemory<byte> data)
        {
            if (_isDisposed || data.Length == 0) return;

            var (prefix, payload) = UnpackBitStream(data.ToArray());

            if (prefix == 0)
            {
            }
            else if (prefix == 1)
            {
                // Handle Handshake validation and control frames
                if (payload.Length > 0 && (char)payload[0] == 'V')
                {
                    try
                    {
                        // Skip the leading 'V' flag identifier character
                        using var ms = new MemoryStream(payload, 1, payload.Length - 1);
                        using var reader = new BinaryReader(ms, Encoding.UTF8);

                        // --- STEP 2 & 3: Read version and check if it matches 'VOTE-1.1' ---
                        string versionString = reader.ReadString();
                        if (versionString != "VOTE-1.1")
                        {
                            _handshakeCompletionSource?.TrySetResult(false);
                            return;
                        }

                        // --- STEP 4: Unpack continuous instruction array elements ---
                        int arrayLength = reader.ReadInt32();
                        for (int i = 0; i < arrayLength; i++)
                        {
                            int instructionsLength = reader.ReadInt32();
                            byte[] instructions = reader.ReadBytes(instructionsLength);
                            long typeId = reader.ReadInt64();

                            // Instantiate and map container via assembly specifications
                            var opType = new Operation.OperationType(instructions, typeId, Env);
                            _operationTypes[typeId] = opType;
                        }

                        // Complete connection handshake workflow successfully
                        _handshakeCompletionSource?.TrySetResult(true);
                    }
                    catch
                    {
                        _handshakeCompletionSource?.TrySetResult(false);
                    }
                }
                else
                {
                    string command = Encoding.UTF8.GetString(payload).TrimEnd('\0');
                    if (command == "END")
                    {
                        OnDisconnect?.Invoke(this, (SocketRequestHandler, "Session ended normally."));
                        Dispose();
                    }
                }
            }
        }

        private void HandleSocketDisconnection(object? sender, string reason)
        {
            if (!_isDisposed && !CancellationToken.IsCancellationRequested)
            {
                _ = ReconnectLoopAsync();
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            GC.SuppressFinalize(this);

            _handshakeCompletionSource?.TrySetResult(false);

            SocketRequestHandler.OnDataReceived -= HandleIncomingData;
            SocketRequestHandler.OnDisconnected -= HandleSocketDisconnection;

            try
            {
                SocketRequestHandler.Dispose();
            }
            catch { }

            foreach (var op in _pendingOperations.Values)
            {
                op.Dispose();
            }
            _pendingOperations.Clear();
            _operationTypes.Clear();

            _connectionLock.Dispose();
        }
    }
}