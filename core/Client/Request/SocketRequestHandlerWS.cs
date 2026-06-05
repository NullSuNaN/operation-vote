using System.Net.WebSockets;
using System.Text;
namespace operation_vote.Client.Request
{
  public class SocketRequestHandlerWS : ISocketRequestHandler
  {
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private bool _isDisposed;

    public bool IsConnected => _webSocket != null && _webSocket.State == WebSocketState.Open;

    public event EventHandler<ReadOnlyMemory<byte>>? OnDataReceived;
    public event EventHandler<string>? OnDisconnected;

    public async Task ConnectAsync(string uri, CancellationToken cancellationToken = default)
    {
      if (IsConnected) return;

      // Normalize protocol scheme if needed (e.g., ensure it starts with ws:// or wss://)
      string targetUri = uri;
      if (targetUri.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
      {
        targetUri = targetUri.Replace("tcp://", "ws://", StringComparison.OrdinalIgnoreCase);
      }

      _webSocket = new ClientWebSocket();
      _cts = new CancellationTokenSource();

      await _webSocket.ConnectAsync(new Uri(targetUri), cancellationToken);
    }

    public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
    {
      if (!IsConnected || _webSocket == null)
        throw new InvalidOperationException("WebSocket Client is not connected.");

      var buffer = new ArraySegment<byte>(data);

      // WebSockets require you to specify whether you are sending binary or text. 
      // Defaulting to Binary for raw byte arrays.
      await _webSocket.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
    }

    public async Task SendTextAsync(string message, CancellationToken cancellationToken = default)
    {
      if (!IsConnected || _webSocket == null)
        throw new InvalidOperationException("WebSocket Client is not connected.");

      byte[] bytes = Encoding.UTF8.GetBytes(message);
      var buffer = new ArraySegment<byte>(bytes);

      await _webSocket.SendAsync(buffer, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    public async Task StartListeningAsync(CancellationToken cancellationToken = default)
    {
      if (!IsConnected || _webSocket == null || _cts == null)
        throw new InvalidOperationException("Cannot start listening; client is not connected.");

      using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
      var token = linkedCts.Token;

      // 4KB initial buffer chunk size
      byte[] chunkBuffer = new byte[4096];

      try
      {
        while (!token.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
        {
          // For large payloads, WebSockets chunk the data. 
          // We'll use a MemoryStream to stitch chunks together if endOfMessage is false.
          using var messageStream = new System.IO.MemoryStream();
          WebSocketReceiveResult result;

          do
          {
            result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(chunkBuffer), token);

            if (result.MessageType == WebSocketMessageType.Close)
            {
              HandleDisconnect("Server initiated close sequence.");
              return;
            }

            await messageStream.WriteAsync(chunkBuffer, 0, result.Count, token);

          } while (!result.EndOfMessage); // Loop until the entire frame is pulled down

          // Fire the unified event with the complete, reconstituted payload
          byte[] completeMessage = messageStream.ToArray();
          OnDataReceived?.Invoke(this, new ReadOnlyMemory<byte>(completeMessage));
        }
      }
      catch (Exception ex) when (ex is WebSocketException || ex is ObjectDisposedException || ex is OperationCanceledException)
      {
        // Expected cleanup triggers on network drop or manual cancellation
      }
      finally
      {
        HandleDisconnect("WebSocket connection closed or lost.");
      }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
      if (_webSocket == null || _webSocket.State != WebSocketState.Open) return;

      _cts?.Cancel();

      try
      {
        // Gracefully tell the server we are shutting down
        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing", cancellationToken);
      }
      catch
      {
        // Sink errors if connection dropped aggressively mid-close
      }
      finally
      {
        HandleDisconnect("Client requested explicit disconnect.");
      }
    }

    private void HandleDisconnect(string reason)
    {
      if (_webSocket == null) return;

      _webSocket.Dispose();
      _webSocket = null;

      OnDisconnected?.Invoke(this, reason);
    }

    public void Dispose()
    {
      if (_isDisposed) return;
      HandleDisconnect("Client is closed.");

      _cts?.Cancel();
      _cts?.Dispose();
      _webSocket?.Dispose();

      _isDisposed = true;
      GC.SuppressFinalize(this);
    }

    public ISocketRequestHandler Construct()
    {
      return new SocketRequestHandlerWS();
    }
  }
}
