namespace operation_vote.Client.Request
{
	public interface ISocketRequestHandler : IDisposable
	{
		/// <summary>
		/// Gets the current connection state of the socket.
		/// </summary>
		bool IsConnected { get; }

		/// <summary>
		/// Establishes a connection to the remote endpoint.
		/// </summary>
		/// <param name="uri">The target URI or endpoint string (e.g., "wss://example.com" or "tcp://127.0.0.1:8080")</param>
		Task ConnectAsync(string uri, CancellationToken cancellationToken = default);

		/// <summary>
		/// Sends data asynchronously to the server.
		/// </summary>
		Task SendAsync(byte[] data, CancellationToken cancellationToken = default);

		/// <summary>
		/// Sends text-based data asynchronously. Handy for WebSockets, 
		/// but can be converted to bytes for Raw TCP.
		/// </summary>
		Task SendTextAsync(string message, CancellationToken cancellationToken = default);

		/// <summary>
		/// Starts listening for incoming data. Fires the OnMessageReceived event when data arrives.
		/// </summary>
		Task StartListeningAsync(CancellationToken cancellationToken = default);

		/// <summary>
		/// Gracefully closes the connection.
		/// </summary>
		Task DisconnectAsync(CancellationToken cancellationToken = default);

		/// <summary>
		/// Event raised whenever raw bytes are received from the socket.
		/// </summary>
		event EventHandler<ReadOnlyMemory<byte>> OnDataReceived;

		/// <summary>
		/// Event raised when the socket connection is lost or closed.
		/// </summary>
		event EventHandler<string> OnDisconnected;
	}
}