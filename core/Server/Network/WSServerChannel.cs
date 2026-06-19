using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.Versioning;

namespace operation_vote.Server.Network
{
  [UnsupportedOSPlatform("browser")]
  public class WSServerChannel : IServerChannel
  {
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<ClientInfo, WebSocket> _sockets = new();

    // Public CORS Rule Configurations
    public string AllowedOrigins { get; set; } = "*";
    public string AllowedMethods { get; set; } = "GET, POST, OPTIONS";
    public string AllowedHeaders { get; set; } = "Content-Type, Authorization";

    public event EventHandler<ClientInfo>? OnChannelClientConnected;
    public event EventHandler<(ClientInfo Client, string Reason)>? OnChannelClientDisconnected;
    public event EventHandler<(ClientInfo Client, byte[] Payload)>? OnChannelDataReceived;
    public event EventHandler<(ClientInfo Client, byte[] Payload)>? OnChannelDataSent;


    public WSServerChannel(string prefixUri)
    {
      _listener = new HttpListener();
      _listener.Prefixes.Add(prefixUri); // Format: "http://127.0.0.1:9056/"
    }

    public async Task StartAsync(User unauthorizedUser)
    {
      _listener.Start();
      try
      {
        while (!_cts.Token.IsCancellationRequested)
        {
          HttpListenerContext context = await _listener.GetContextAsync();

          // Apply standard CORS policy headers to every incoming HTTP request frame
          ApplyCorsHeaders(context);

          // Handle HTTP OPTIONS Preflight Request
          if (context.Request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
          {
            context.Response.StatusCode = 204; // No Content for successful preflight
            context.Response.Close();
            continue;
          }

          if (context.Request.IsWebSocketRequest)
          {
            _ = Task.Run(() => ProcessWebSocketHandshakeAsync(context, unauthorizedUser), _cts.Token);
          }
          else
          {
            context.Response.StatusCode = 400;
            context.Response.Close();
          }
        }
      }
      catch (Exception) { }
    }

    private void ApplyCorsHeaders(HttpListenerContext context)
    {
      // If checking a specific dynamic origin list, read context.Request.Headers["Origin"]
      context.Response.Headers.Add("Access-Control-Allow-Origin", AllowedOrigins);
      context.Response.Headers.Add("Access-Control-Allow-Methods", AllowedMethods);
      context.Response.Headers.Add("Access-Control-Allow-Headers", AllowedHeaders);
      context.Response.Headers.Add("Access-Control-Max-Age", "86400"); // Cache preflight response for 24 hours
    }

    private async Task ProcessWebSocketHandshakeAsync(HttpListenerContext context, User unauthorizedUser)
    {
      try
      {
        HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
        WebSocket webSocket = wsContext.WebSocket;
        Guid clientId = Guid.NewGuid();
        ClientInfo client = new(this, clientId, unauthorizedUser);
        _sockets[client] = webSocket;

        OnChannelClientConnected?.Invoke(this, client);
        await HandleWebSocketLoopAsync(client, webSocket);
      }
      catch
      {
        context.Response.StatusCode = 500;
        context.Response.Close();
      }
    }

    private async Task HandleWebSocketLoopAsync(ClientInfo client, WebSocket webSocket)
    {
      byte[] buffer = new byte[4096];
      string reason = "WebSocket closed normally.";

      try
      {
        while (webSocket.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
        {
          using var ms = new MemoryStream();
          WebSocketReceiveResult result;
          do
          {
            result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
              reason = "Remote client closed WebSocket session.";
              break;
            }
            ms.Write(buffer, 0, result.Count);
          } while (!result.EndOfMessage);

          if (webSocket.State != WebSocketState.Open || result.MessageType == WebSocketMessageType.Close) break;

          OnChannelDataReceived?.Invoke(this, (client, ms.ToArray()));
        }
      }
      catch (Exception ex) { reason = ex.Message; }
      finally
      {
        _sockets.TryRemove(client, out _);
        try { await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None); } catch { }
        webSocket.Dispose();
        OnChannelClientDisconnected?.Invoke(this, (client, reason));
      }
    }

    public async Task SendToClientAsync(ClientInfo client, byte[] data)
    {
      if (_sockets.TryGetValue(client, out var socket) && socket.State == WebSocketState.Open)
      {
        await socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, _cts.Token);
        OnChannelDataSent?.Invoke(this, (client, data));
      }
    }

    public async Task ResetAsync(ClientInfo Client)
    {
      if (_sockets.TryGetValue(Client, out var socket) && socket.State == WebSocketState.Open)
      {
        socket.Abort();
      }
    }

    public async Task BroadcastAsync(byte[] data)
    {
      foreach (var clientId in _sockets.Keys)
      {
        try { await SendToClientAsync(clientId, data); } catch { }
      }
    }

    public void Stop()
    {
      _cts.Cancel();
      try { _listener.Stop(); } catch { }
      foreach (var socket in _sockets.Values) socket.Dispose();
      _sockets.Clear();
    }

    public void Dispose() => Stop();
  }
}