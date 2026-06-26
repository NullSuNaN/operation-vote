using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using operation_vote.Client.Request;
using operation_vote.Shared;

namespace operation_vote.Client
{
	public class VotingClient<T> : IDisposable where T : ISocketRequestHandler
	{
		public readonly T SocketRequestHandler;
		private string _targetURI;
		private readonly SemaphoreSlim _connectionLock = new(1, 1);

		// Local storage for parsed Operation Types initialized during connection handshake
		private readonly ConcurrentDictionary<long, Operation.OperationType> _operationTypes = new();

		private const int RECONNECT_LIMIT = 3;
		private int _reconnectAttempts;
		private int _isReconnecting; // 0 = false, 1 = true for atomic thread-safety
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
		public AuthenticationClient.AuthenticationData? authenticationData = null;

		public event EventHandler<(T, string)>? OnDisconnect;
		public event EventHandler? OnConnectionFinished;
		public event EventHandler<bool>? OnAuthorizationFinished;
		private int voteMultiplierCache = ProtocolInfo.ClientDefaultVoteMultiplier;
		public int VoteMultiplier => Volatile.Read(ref voteMultiplierCache);
		private volatile string? user = null;
		public string? User => user;

		public event EventHandler<(int Original, int New)>? OnVoteMultiplierChange;
		/// <summary>
		/// Triggered before a reconnection happens and the operation types are reloaded.<br/>
		/// The original operation types would all be disposed and new ones would be added.
		/// </summary>
		public event EventHandler? BeforeOperationsReload;
		/// <summary>
		/// Triggered after a reconnection happens and the operation types are reloaded.<br/>
		/// The original operation types would all be disposed and new ones would be added.<br/>
		/// The parameter indicates if the reload succeed. If not, it will disconnect.<br/>
		/// You can resend your active operations here.
		/// </summary>
		public event EventHandler<bool>? AfterOperationsReload;
		/// <summary>
		/// Triggered both when authorization successes(before <see cref="OnAuthorizationFinished"/>) and the server forces to change the user.
		/// </summary>
		public event EventHandler<string?>? OnUserChanged;
		private TaskCompletionSource<string> AuthenticationFetchToken = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private TaskCompletionSource<bool> AuthenticationResult = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

				{ // Initialize connection
					using var ms = new MemoryStream();
					using var writer = new BinaryWriter(ms, Encoding.UTF8);

					writer.Write(true);
					writer.Write(ProtocolInfo.ClientCommands.InitializeCommand);
					await SocketRequestHandler.SendAsync(ms.ToArray(), CancellationToken).ConfigureAwait(false);
				}

				// Wait for the server data arrival to execute version checks
				bool initialized = await _handshakeCompletionSource.Task.ConfigureAwait(false);
				if (!initialized)
				{
					await SocketRequestHandler.DisconnectAsync(CancellationToken).ConfigureAwait(false);
					OnDisconnect?.Invoke(this, (SocketRequestHandler, "Failed to initialize the connection."));
					Dispose();
				}

				// Authorize if the client is logged in
				var _authenticationData = Volatile.Read(ref authenticationData);
				if (_authenticationData != null)
				{
					AuthenticationFetchToken = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
					AuthenticationResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
					bool authSuccess = await AuthenticationClient.AuthenticateAsync(
						Volatile.Read(ref _authenticationData),
						async data =>
						{
							using var ms = new MemoryStream();
							using var writer = new BinaryWriter(ms, Encoding.UTF8);
							writer.Write(true);
							writer.Write(ProtocolInfo.ClientCommands.AuthenticateRequestCommand);
							writer.Flush();
							await SocketRequestHandler.SendAsync(ms.ToArray());
							return await AuthenticationFetchToken.Task;
						},
						async data =>
						{
							using var ms = new MemoryStream();
							using var writer = new BinaryWriter(ms, Encoding.UTF8);
							writer.Write(true);
							writer.Write(ProtocolInfo.ClientCommands.AuthenticateResultCommand);
							writer.Write7BitEncodedInt(data.Length);
							writer.Write(data);
							writer.Flush();
							await SocketRequestHandler.SendAsync(ms.ToArray());
							return await AuthenticationResult.Task;
						}
					);
					if (authSuccess)
					{
						user = _authenticationData.Username;
						OnUserChanged?.Invoke(this, _authenticationData.Username);
					}
					OnAuthorizationFinished?.Invoke(this, authSuccess);
				}
				{ // Establish Voting
					using var ms = new MemoryStream();
					using var writer = new BinaryWriter(ms, Encoding.UTF8);
					writer.Write(true);
					writer.Write(ProtocolInfo.ClientCommands.RegisterInstanceCommand);
					await SocketRequestHandler.SendAsync(ms.ToArray(), CancellationToken).ConfigureAwait(false);
				}
				OnConnectionFinished?.Invoke(this, EventArgs.Empty);
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

		public async Task<bool> SendOperationAsync(Operation operation, CancellationToken token = default)
		{
			if (Disconnected || operation.IsDisposed) return false;

			using var ms = new MemoryStream();
			using var writer = new BinaryWriter(ms, Encoding.UTF8);

			writer.Write(false);
			if (!operation.Serialize(writer)) return false;

			while (!Disconnected && !token.IsCancellationRequested)
			{
				bool locked = false;
				try
				{
					_connectionLock.Wait(token);
					locked = true;
					if (operation.Type.IsDisposed || token.IsCancellationRequested) return false;
					await SocketRequestHandler.SendAsync(ms.ToArray(), token).ConfigureAwait(false);
					break;
				}
				catch (TaskCanceledException) { return false; }
				finally { if (locked) _connectionLock.Release(); }
			}
			return true;
		}

		private async Task ReconnectLoopAsync()
		{
			if (Disconnected) return;

			// Prevent multiple reconnection loops from running simultaneously
			if (Interlocked.Exchange(ref _isReconnecting, 1) == 1)
				return;

			BeforeOperationsReload?.Invoke(this, EventArgs.Empty);

			try
			{
				// Prevent simultaneous execution of connection lifecycle logic across parallel threads
				await _connectionLock.WaitAsync(CancellationToken).ConfigureAwait(false);
				try
				{
					while (!Disconnected && !CancellationToken.IsCancellationRequested)
					{
						try
						{
							if (SocketRequestHandler.IsConnected)
							{
								await SocketRequestHandler.DisconnectAsync(CancellationToken).ConfigureAwait(false);
							}

							string cleanUri;
							lock (_uriLock) { cleanUri = _targetURI.TrimStart('+'); }

							await SocketRequestHandler.ConnectAsync(cleanUri, CancellationToken).ConfigureAwait(false);

							_handshakeCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

							// Start inbound listener processing pathway
							_ = Task.Run(() => SocketRequestHandler.StartListeningAsync(CancellationToken), CancellationToken);

							{ // 1. Initialize connection
								using var ms = new MemoryStream();
								using var writer = new BinaryWriter(ms, Encoding.UTF8);

								writer.Write(true);
								writer.Write(ProtocolInfo.ClientCommands.InitializeCommand);
								await SocketRequestHandler.SendAsync(ms.ToArray(), CancellationToken).ConfigureAwait(false);
							}

							// Wait for the server data arrival to execute version checks
							bool initialized = await _handshakeCompletionSource.Task.ConfigureAwait(false);
							if (!initialized)
							{
								_operationTypes.Clear();
								throw new InvalidOperationException("Failed to reconnect.");
							}
							// 2. Re-authorize
							var _authenticationData = Volatile.Read(ref authenticationData);
							if (_authenticationData != null)
							{
								AuthenticationFetchToken = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
								AuthenticationResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
								bool authSuccess = await AuthenticationClient.AuthenticateAsync(
									Volatile.Read(ref _authenticationData),
									async data =>
									{
										using var ms = new MemoryStream();
										using var writer = new BinaryWriter(ms, Encoding.UTF8);
										writer.Write(true);
										writer.Write(ProtocolInfo.ClientCommands.AuthenticateRequestCommand);
										writer.Flush();
										await SocketRequestHandler.SendAsync(ms.ToArray());
										return await AuthenticationFetchToken.Task;
									},
									async data =>
									{
										using var ms = new MemoryStream();
										using var writer = new BinaryWriter(ms, Encoding.UTF8);
										writer.Write(true);
										writer.Write(ProtocolInfo.ClientCommands.AuthenticateResultCommand);
										writer.Write7BitEncodedInt(data.Length);
										writer.Write(data);
										writer.Flush();
										await SocketRequestHandler.SendAsync(ms.ToArray());
										return await AuthenticationResult.Task;
									}
								);
								var originalUser = user;
								if (authSuccess)
								{
									user = _authenticationData.Username;
								}
								else
								{
									user = null;
								}
								if (user != originalUser)
									OnUserChanged?.Invoke(this, _authenticationData.Username);
								OnAuthorizationFinished?.Invoke(this, authSuccess);
							}
							{ // 3. Re-send Handshake registration
								using var ms = new MemoryStream();
								using var writer = new BinaryWriter(ms, Encoding.UTF8);
								writer.Write(true);
								writer.Write(ProtocolInfo.ClientCommands.RegisterInstanceCommand);

								await SocketRequestHandler.SendAsync(ms.ToArray(), CancellationToken).ConfigureAwait(false);
							}

							Interlocked.Exchange(ref _reconnectAttempts, 0);

							try { AfterOperationsReload?.Invoke(this, true); } finally { }
							break;
						}
						catch (Exception ex)
						{
							Console.WriteLine($"Reconnection attempt {Volatile.Read(ref _reconnectAttempts) + 1} failed: {ex.Message}");
							if (Interlocked.Increment(ref _reconnectAttempts) >= RECONNECT_LIMIT)
							{
								AfterOperationsReload?.Invoke(this, false);
								OnDisconnect?.Invoke(this, (SocketRequestHandler, "Connection is interrupted."));
								Dispose();
								break;
							}
							// Wait 1 second before retrying
							await Task.Delay(1000, CancellationToken).ConfigureAwait(false);
						}
					}
				}
				finally
				{
					_connectionLock.Release();
				}
			}
			finally
			{
				Interlocked.Exchange(ref _isReconnecting, 0);
			}
		}

		private void HandleIncomingData(object? sender, ReadOnlyMemory<byte> data)
		{
			if (Disconnected || data.Length == 0) return;
			using var ms = new MemoryStream(data.ToArray());
			using var reader = new BinaryReader(ms, Encoding.UTF8);
			string command = reader.ReadString();
			switch (command)
			{
				case ProtocolInfo.ServerCommands.InitializeCommand:
					try
					{
						string versionString = reader.ReadString();
						if (versionString != $"VOTE-{ProtocolInfo.Version}")
						{
							_handshakeCompletionSource?.TrySetResult(false);
							return;
						}

						int arrayLength = reader.ReadInt32();
						_operationTypes.Clear();
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
					break;
				case ProtocolInfo.ServerCommands.EndSessionCommand:
					OnDisconnect?.Invoke(this, (SocketRequestHandler, "Session ended normally."));
					Dispose();
					break;
				case ProtocolInfo.ServerCommands.AuthenticateChallengeCommand:
					AuthenticationFetchToken.TrySetResult(reader.ReadString());
					break;
				case ProtocolInfo.ServerCommands.AuthenticateResultCommand:
					AuthenticationResult.TrySetResult(reader.ReadBoolean());
					break;
				case ProtocolInfo.ServerCommands.UpdateStatusCommand:
					string stat = reader.ReadString();
					switch (stat)
					{
						case "MUL":
							int multiplier = reader.Read7BitEncodedInt();
							int oldMultiplier = Interlocked.Exchange(ref voteMultiplierCache, multiplier);
							OnVoteMultiplierChange?.Invoke(this, (oldMultiplier, multiplier));
							break;
						case "ACO":
							bool authorized = reader.ReadBoolean();
							string? _user;
							if (authorized)
								_user = reader.ReadString();
							else
								_user = null;
							user = _user;
							OnUserChanged?.Invoke(this, user);
							int newMultiplier = reader.Read7BitEncodedInt();
							int _oldMultiplier = Interlocked.Exchange(ref voteMultiplierCache, newMultiplier);
							if (_oldMultiplier != newMultiplier)
								OnVoteMultiplierChange?.Invoke(this, (_oldMultiplier, newMultiplier));
							break;
					}
					break;
				default:
					throw new ProtocolViolationException($"Invalid command: {command}");
			}
		}

		private void HandleSocketDisconnection(object? sender, Func<(string reason, bool isNormal)> reason)
		{
			if (!Disconnected && !CancellationToken.IsCancellationRequested)
			{
				_ = Task.Run(() => ReconnectLoopAsync());
			}
		}

		public async Task DisposeAsync()
		{
			// Thread-safe isolation check flag
			if (Interlocked.Exchange(ref _isDisposedState, 1) == 1) return;
			await Dispose_();
		}
		public void Dispose()
		{
			GC.SuppressFinalize(this);
			if (Interlocked.Exchange(ref _isDisposedState, 1) == 1) return;
			Dispose_().GetAwaiter().GetResult();
		}
		private async Task Dispose_()
		{
			_handshakeCompletionSource?.TrySetResult(false);

			SocketRequestHandler.OnDataReceived -= HandleIncomingData;
			SocketRequestHandler.OnDisconnected -= HandleSocketDisconnection;

			try
			{
				using var ms = new MemoryStream();
				using var writer = new BinaryWriter(ms, Encoding.UTF8);
				writer.Write(true);
				writer.Write(ProtocolInfo.ClientCommands.RegisterInstanceCommand);

				await SocketRequestHandler.SendAsync(ms.ToArray(), CancellationToken).ConfigureAwait(false);
				SocketRequestHandler.Dispose();
			}
			catch { }

			_operationTypes.Clear();

			_connectionLock.Dispose();
		}
	}
}