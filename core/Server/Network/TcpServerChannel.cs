using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace operation_vote.Server.Network
{
  [UnsupportedOSPlatform("browser")]
  public class TcpServerChannel(string ipAddress, int port) : IServerChannel
  {
    private readonly TcpListener _listener = new(IPAddress.Parse(ipAddress), port);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<ClientInfo, TcpClient> _clients = new();

    public event EventHandler<ClientInfo>? OnChannelClientConnected;
    public event EventHandler<(ClientInfo Client, string Reason)>? OnChannelClientDisconnected;
    public event EventHandler<(ClientInfo Client, byte[] Payload)>? OnChannelDataReceived;

    public async Task StartAsync(User unauthorizedUser)
    {
      _listener.Start();
      try
      {
        while (!_cts.Token.IsCancellationRequested)
        {
          TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token);
          Guid clientId = Guid.NewGuid();
          ClientInfo clientInfo = new(this, clientId, unauthorizedUser);
          _clients[clientInfo] = client;

          OnChannelClientConnected?.Invoke(this, clientInfo);
          _ = Task.Run(() => HandleClientLoopAsync(clientInfo, client), _cts.Token);
        }
      }
      catch (OperationCanceledException) { }
    }

    private async Task HandleClientLoopAsync(ClientInfo clientInfo, TcpClient client)
    {
      using var stream = client.GetStream();
      byte[] lengthBuffer = new byte[4];
      string reason = "Connection closed cleanly.";

      try
      {
        while (client.Connected && !_cts.Token.IsCancellationRequested)
        {
          if (!await TryReadExactlyAsync(stream, lengthBuffer, 4))
          {
            reason = "Reached End-of-Stream.";
            break;
          }

          // FIX: Convert from Big-Endian network byte order to native system architecture
          if (BitConverter.IsLittleEndian)
          {
            Array.Reverse(lengthBuffer);
          }
          int length = BitConverter.ToInt32(lengthBuffer, 0);

          if (length <= 0 || length > 10 * 1024 * 1024)
          {
            reason = $"Protocol mismatch frame size violation: ({length} bytes).";
            break;
          }

          byte[] packetBuffer = new byte[length];
          if (!await TryReadExactlyAsync(stream, packetBuffer, length))
          {
            reason = "Truncated payload stream exception.";
            break;
          }

          OnChannelDataReceived?.Invoke(this, (clientInfo, packetBuffer));
        }
      }
      catch (Exception ex)
      {
        reason = ex.Message;
      }
      finally
      {
        _clients.TryRemove(clientInfo, out _);
        client.Close();
        OnChannelClientDisconnected?.Invoke(this, (clientInfo, reason));
      }
    }

    public async Task SendToClientAsync(ClientInfo client, byte[] data)
    {
      if (_clients.TryGetValue(client, out var tcpClient) && tcpClient.Connected)
      {
        var stream = tcpClient.GetStream();
        byte[] lengthHeader = BitConverter.GetBytes(data.Length);

        // FIX: Convert from native system architecture to Big-Endian network byte order
        if (BitConverter.IsLittleEndian)
        {
          Array.Reverse(lengthHeader);
        }

        await stream.WriteAsync(lengthHeader, 0, 4);
        await stream.WriteAsync(data, 0, data.Length);
      }
    }

    public async Task BroadcastAsync(byte[] data)
    {
      foreach (var clientId in _clients.Keys)
      {
        try
        {
          // SendToClientAsync handles its own Big-Endian length framing internally
          await SendToClientAsync(clientId, data);
        }
        catch { }
      }
    }

    private static async Task<bool> TryReadExactlyAsync(NetworkStream stream, byte[] buffer, int length)
    {
      int total = 0;
      while (total < length)
      {
        int read = await stream.ReadAsync(buffer, total, length - total);
        if (read == 0) return false;
        total += read;
      }
      return true;
    }

    public void Stop()
    {
      _cts.Cancel();
      _listener.Stop();
      foreach (var client in _clients.Values) client.Close();
      _clients.Clear();
    }

    public void Dispose() => Stop();
  }
}