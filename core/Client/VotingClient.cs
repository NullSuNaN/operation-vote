using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using operation_vote.Client.Request;

namespace operation_vote.Client
{
	public class VotingClient<T> : IDisposable where T : ISocketRequestHandler
	{
		public readonly T SocketRequestHandler;
		private string _targetURI;
		private readonly ConcurrentDictionary<long, Operation> _pendingOperations = new();
		private readonly SemaphoreSlim _connectionLock = new(1, 1);

		// Local storage for parsed Operation Types initialized during connection handshake
		private readonly ConcurrentDictionary<long, Operation.OperationType> _operationTypes = new();

		private const int RECONNECT_LIMIT = 3;
		private int _reconnectAttempts;
		private int _isDisposedState; // 0 = false, 1 = true for atomic thread-safety
		public bool Disconnected => Volatile.Read(ref _isDisposedState) == 1;

		// Tracks the multi-step initialization handshake frame asynchronously
		private TaskCompletionSource<bool>? _handshakeCompletionSource;

		public string TargetURI
		{
			get
			{
				lock (_uriLock) return _targetURI;
			}
			set
			{
				lock (_uriLock)
				{
					if (_targetURI == value) return;
					_targetURI = value;
				}
				// Fire-and-forget safely monitored background task
				_ = Task.Run(() => ReconnectLoopAsync());
			}
		}
		private readonly Lock _uriLock = new();

		public readonly CancellationToken CancellationToken;
		public readonly VotingEnv Env = new();
		public ReadOnlyDictionary<long, Operation.OperationType> OperationTypes => _operationTypes.AsReadOnly();

		public event EventHandler<(T, string)>? OnDisconnect;

		public VotingClient(T socketRequestHandler, string uri, CancellationToken token = default)
		{
			SocketRequestHandler = socketRequestHandler ?? throw new ArgumentNullException(nameof(socketRequestHandler));
			_targetURI = uri ?? throw new ArgumentNullException(nameof(uri));
			CancellationToken = token;

			SocketRequestHandler.OnDataReceived += HandleIncomingData;
			SocketRequestHandler.OnDisconnected += HandleSocketDisconnection;
		}

		public async Task ConnectAsync()
		{
			if (Disconnected) return;

			await _connectionLock.WaitAsync(CancellationToken).ConfigureAwait(false);
			try
			{
				if (Disconnected) return;

				_handshakeCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

				string cleanUri;
				lock (_uriLock) { cleanUri = _targetURI.TrimStart('+'); }

				await SocketRequestHandler.ConnectAsync(cleanUri, CancellationToken).ConfigureAwait(false);

				Interlocked.Exchange(ref _reconnectAttempts, 0);

				// Start inbound listener processing pathway
				_ = Task.Run(() => SocketRequestHandler.StartListeningAsync(CancellationToken), CancellationToken);

				// --- STEP 1: Pack and transmit Bit 1 + 'INIT' Handshake Frame ---
				byte[] initTextBytes = Encoding.UTF8.GetBytes("INIT");
				byte[] initPacket = PackBitStream(1, initTextBytes);
				await SocketRequestHandler.SendAsync(initPacket, CancellationToken).ConfigureAwait(false);

				// Wait for the server data arrival to execute version checks
				bool initialized = await _handshakeCompletionSource.Task.ConfigureAwait(false);
				if (!initialized)
				{
					await SocketRequestHandler.DisconnectAsync(CancellationToken).ConfigureAwait(false);
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
			if (Disconnected || operation.IsDisposed) return;

			_pendingOperations[operation.Id] = operation;

			byte[] rawOperationBytes = operation.ToByteArray();
			byte[] fullPacket = PackBitStream(0, rawOperationBytes);

			await SocketRequestHandler.SendAsync(fullPacket, token).ConfigureAwait(false);
		}

		private async Task ReconnectLoopAsync()
		{
			if (Disconnected) return;

			// Prevent simultaneous execution of connection lifecycle logic across parallel threads
			await _connectionLock.WaitAsync(CancellationToken).ConfigureAwait(false);
			try
			{
				if (Disconnected) return;

				if (SocketRequestHandler.IsConnected)
				{
					await SocketRequestHandler.DisconnectAsync(CancellationToken).ConfigureAwait(false);
				}

				string cleanUri;
				lock (_uriLock) { cleanUri = _targetURI.TrimStart('+'); }

				await SocketRequestHandler.ConnectAsync(cleanUri, CancellationToken).ConfigureAwait(false);

				Interlocked.Exchange(ref _reconnectAttempts, 0);

				// 1. Replay cached non-disposed operations safely across thread modifications
				foreach (var pair in _pendingOperations)
				{
					if (pair.Value.IsDisposed)
					{
						_pendingOperations.TryRemove(pair.Key, out _);
						continue;
					}

					byte[] rawOpBytes = pair.Value.ToByteArray();
					byte[] replayPacket = PackBitStream(0, rawOpBytes);

					await SocketRequestHandler.SendAsync(replayPacket, CancellationToken).ConfigureAwait(false);
				}

				// 2. Re-send Handshake registration
				byte[] regCmdBytes = Encoding.UTF8.GetBytes("REG");
				byte[] registrationPacket = PackBitStream(1, regCmdBytes);

				await SocketRequestHandler.SendAsync(registrationPacket, CancellationToken).ConfigureAwait(false);

				_ = Task.Run(() => SocketRequestHandler.StartListeningAsync(CancellationToken), CancellationToken);
			}
			catch
			{
				if (Interlocked.Increment(ref _reconnectAttempts) >= RECONNECT_LIMIT)
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
			if (data == null || data.Length == 0) return (0, Array.Empty<byte>());

			byte prefix = (byte)((data[0] & 0x80) >> 7);

			int totalPayloadBits = (data.Length * 8) - 1;
			int targetedBytesCount = (totalPayloadBits + 7) / 8; // Corrected ceiling division roundup

			byte[] payload = new byte[targetedBytesCount];

			for (int i = 0; i < payload.Length; i++)
			{
				byte currentPart = (byte)(data[i] << 1);
				byte nextPart = (i + 1 < data.Length) ? (byte)((data[i + 1] & 0x80) >> 7) : (byte)0;
				payload[i] = (byte)(currentPart | nextPart);
			}

			return (prefix, payload);
		}

		private void HandleIncomingData(object? sender, ReadOnlyMemory<byte> data)
		{
			if (Disconnected || data.Length == 0) return;

			var (prefix, payload) = UnpackBitStream(data.ToArray());

			if (prefix == 0)
			{
				// Handle payload data standard pathways down-stream
			}
			else if (prefix == 1)
			{
				if (payload.Length > 0 && (char)payload[0] == 'V')
				{
					try
					{
						using var ms = new MemoryStream(payload, 1, payload.Length - 1);
						using var reader = new BinaryReader(ms, Encoding.UTF8);

						string versionString = reader.ReadString();
						if (versionString != "VOTE-1.1")
						{
							_handshakeCompletionSource?.TrySetResult(false);
							return;
						}

						int arrayLength = reader.ReadInt32();
						for (int i = 0; i < arrayLength; i++)
						{
							int instructionsLength = reader.ReadInt32();
							byte[] instructions = reader.ReadBytes(instructionsLength);
							long typeId = reader.ReadInt64();

							var opType = new Operation.OperationType(instructions, typeId, Env);
							_operationTypes[typeId] = opType;
						}

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
			if (!Disconnected && !CancellationToken.IsCancellationRequested)
			{
				_ = Task.Run(() => ReconnectLoopAsync());
			}
		}

		public void Dispose()
		{
			// Thread-safe isolation check flag
			if (Interlocked.Exchange(ref _isDisposedState, 1) == 1) return;
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