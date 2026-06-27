using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace operation_vote.Server.Network
{
  [UnsupportedOSPlatform("browser")]
  public class TcpServerChannel(string ipAddress, int port) : IConcurrentServerChannel
  {
    private readonly TcpListener _listener = new(IPAddress.Parse(ipAddress), port);
    private readonly CancellationTokenSource _cts = new();
    
    // Track both the TcpClient and a SemaphoreSlim to lock outbound writes for that specific client
    private readonly ConcurrentDictionary<ClientInfo, (TcpClient Client, SemaphoreSlim WriteLock)> _clients = new();

    public event EventHandler<ClientInfo>? OnChannelClientConnected;
    public event EventHandler<(ClientInfo Client, string Reason)>? OnChannelClientDisconnected;
    public event EventHandler<(ClientInfo Client, byte[] Payload)>? OnChannelDataReceived;
    public event EventHandler<(ClientInfo Client, byte[] Payload)>? OnChannelDataSent;

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
          
          // Initialize a write lock exclusively for this client session
          _clients[clientInfo] = (client, new SemaphoreSlim(1, 1));

          OnChannelClientConnected?.Invoke(this, clientInfo);
          new Thread(() => HandleClientLoopAsync(clientInfo, client).GetAwaiter().GetResult())
          {
            Name = $"Client {clientInfo.ClientId,6}",
            IsBackground = true
          }.Start();
        }
      }
      catch (OperationCanceledException) { }
    }

    private async Task HandleClientLoopAsync(ClientInfo clientInfo, TcpClient client)
    {
      using var stream = client.GetStream();
      byte[] lengthBuffer = new byte[4];
      string reason = "Connection closed.";

      try
      {
        while (client.Connected && !_cts.Token.IsCancellationRequested)
        {
          if (!await TryReadExactlyAsync(stream, lengthBuffer, 4))
          {
            reason = "Reached End-of-Stream.";
            break;
          }

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
        if (_clients.TryRemove(clientInfo, out var cell))
        {
          cell.WriteLock.Dispose();
        }
        client.Close();
        OnChannelClientDisconnected?.Invoke(this, (clientInfo, reason));
      }
    }

    public async Task SendToClientAsync(ClientInfo client, byte[] data)
    {
      if (_clients.TryGetValue(client, out var cell) && cell.Client.Connected)
      {
        // 🟢 Concurrency Fix: Await the write lock to prevent interleaving streams
        await cell.WriteLock.WaitAsync();
        try
        {
          // Double check connection status inside the lock
          if (!cell.Client.Connected) return;

          var stream = cell.Client.GetStream();
          byte[] lengthHeader = BitConverter.GetBytes(data.Length);

          if (BitConverter.IsLittleEndian)
          {
            Array.Reverse(lengthHeader);
          }

          await stream.WriteAsync(lengthHeader, 0, 4);
          await stream.WriteAsync(data, 0, data.Length);
          await stream.FlushAsync(); // Flush immediately to force packet delivery
          
          OnChannelDataSent?.Invoke(this, (client, data));
        }
        finally
        {
          cell.WriteLock.Release();
        }
      }
    }

    public async Task ResetAsync(ClientInfo Client)
    {
      if (_clients.TryGetValue(Client, out var cell) && cell.Client.Connected)
      {
        cell.Client.Close();
      }
    }

    public async Task BroadcastAsync(byte[] data)
    {
      // Parallelize out-bound tasks concurrently across all active client write pipelines
      var tasks = _clients.Keys.Select(client => SendToClientAsync(client, data));
      await Task.WhenAll(tasks);
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
      foreach (var cell in _clients.Values)
      {
        cell.Client.Close();
        cell.WriteLock.Dispose();
      }
      _clients.Clear();
    }

    public void Dispose() => Stop();
  }
}