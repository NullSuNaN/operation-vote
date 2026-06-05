using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;

namespace operation_vote.Client.Request
{// This attribute tells the compiler and IDE to throw warnings/errors 
 // if anyone tries to use this class inside a Browser/Wasm context.
  [UnsupportedOSPlatform("browser")]
  public class SocketRequestHandlerTCP : ISocketRequestHandler
  {
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private bool _isDisposed;

    public bool IsConnected => _tcpClient != null && _tcpClient.Connected;

    // Nullable event handlers to align with Nullable context
    public event EventHandler<ReadOnlyMemory<byte>>? OnDataReceived;
    public event EventHandler<string>? OnDisconnected;

    public async Task ConnectAsync(string uri, CancellationToken cancellationToken = default)
    {
      if (IsConnected) return;

      string cleanUri = uri.Replace("tcp://", "", StringComparison.OrdinalIgnoreCase);
      var parts = cleanUri.Split(':');

      if (parts.Length != 2 || !int.TryParse(parts[1], out int port))
      {
        throw new ArgumentException("Invalid URI format. Expected 'host:port'", nameof(uri));
      }

      string host = parts[0];

      _tcpClient = new();
      _tcpClient.NoDelay = true;
      await _tcpClient.ConnectAsync(host, port, cancellationToken);
      _stream = _tcpClient.GetStream();
      _cts = new CancellationTokenSource();
    }

    public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
    {
      // The null-forgiving operator (!) or direct checks tell the compiler 
      // that if IsConnected is true, _stream is guaranteed not to be null.
      if (!IsConnected || _stream == null)
        throw new InvalidOperationException("TCP Client is not connected.");

      byte[] lengthPrefix = BitConverter.GetBytes(data.Length);
      if (BitConverter.IsLittleEndian)
      {
        Array.Reverse(lengthPrefix);
      }

      await _stream.WriteAsync(lengthPrefix, 0, lengthPrefix.Length, cancellationToken);
      await _stream.WriteAsync(data, 0, data.Length, cancellationToken);
      await _stream.FlushAsync(cancellationToken);
    }

    public async Task SendTextAsync(string message, CancellationToken cancellationToken = default)
    {
      byte[] bytes = Encoding.UTF8.GetBytes(message);
      await SendAsync(bytes, cancellationToken);
    }

    public async Task StartListeningAsync(CancellationToken cancellationToken = default)
    {
      if (!IsConnected || _stream == null || _cts == null)
        throw new InvalidOperationException("Cannot start listening; client is not connected.");

      using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
      var token = linkedCts.Token;

      byte[] lengthBuffer = new byte[4];

      try
      {
        while (!token.IsCancellationRequested && IsConnected)
        {
          if (!await TryReadExactlyAsync(_stream, lengthBuffer, 4, token))
          {
            break;
          }

          if (BitConverter.IsLittleEndian)
          {
            Array.Reverse(lengthBuffer);
          }
          int messageLength = BitConverter.ToInt32(lengthBuffer, 0);

          if (messageLength <= 0) continue;

          byte[] payloadBuffer = new byte[messageLength];
          if (!await TryReadExactlyAsync(_stream, payloadBuffer, messageLength, token))
          {
            break;
          }

          OnDataReceived?.Invoke(this, new ReadOnlyMemory<byte>(payloadBuffer));
        }
      }
      catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is OperationCanceledException)
      {
        // Expected cleanup triggers
      }
      finally
      {
        HandleDisconnect("Connection closed or lost.");
      }
    }

    private static async Task<bool> TryReadExactlyAsync(NetworkStream stream, byte[] buffer, int length, CancellationToken token)
    {
      int totalBytesRead = 0;
      while (totalBytesRead < length)
      {
        int bytesRead = await stream.ReadAsync(buffer, totalBytesRead, length - totalBytesRead, token);
        if (bytesRead == 0)
        {
          return false;
        }
        totalBytesRead += bytesRead;
      }
      return true;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
      if (!IsConnected) return;

      _cts?.Cancel();
      HandleDisconnect("Client requested explicit disconnect.");
      await Task.CompletedTask;
    }

    private void HandleDisconnect(string reason)
    {
      if (_tcpClient == null) return;

      // Using the null-conditional operator (?) safely handles teardown
      _stream?.Close();
      _tcpClient?.Close();

      _stream = null;
      _tcpClient = null;

      OnDisconnected?.Invoke(this, reason);
    }

    public void Dispose()
    {
      if (_isDisposed) return;
      HandleDisconnect("Client is closed.");

      _cts?.Cancel();
      _cts?.Dispose();
      _stream?.Dispose();
      _tcpClient?.Dispose();

      _isDisposed = true;
      GC.SuppressFinalize(this);
    }
  }
}