using System.Text;

namespace operation_vote.Client.Request
{
  public class MockSocketRequestHandler : ISocketRequestHandler
  {
    public bool IsConnected { get; private set; }

    public event EventHandler<ReadOnlyMemory<byte>>? OnDataReceived;
    public event EventHandler<string>? OnDisconnected;

    public Task ConnectAsync(string uri, CancellationToken cancellationToken = default)
    {
      IsConnected = true;
      Console.WriteLine($"   -> [Mock Socket] Successfully connected to: {uri}");
      return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
      IsConnected = false;
      Console.WriteLine("   -> [Mock Socket] Disconnected requested explicitly.");
      return Task.CompletedTask;
    }

    public Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
    {
      if (data.Length == 0)
      {
        Console.WriteLine($"   -> [Wire Sent] No Data is Sent.");
        return Task.CompletedTask;
      }

      byte prefix = (byte)(data[0]>>7);
      string hexPayload = BitConverter.ToString(data, 1);

      if (prefix == 1)
      {
        // Handshake processing: Decode the byte-wise shifted string payload
        byte[] regBytes = new byte[data.Length - 1];
        Buffer.BlockCopy(data, 1, regBytes, 0, regBytes.Length);

        // Reverse the bit-shift left (<< 1) back right (>> 1) to inspect contents
        for (int i = 0; i < regBytes.Length; i++)
        {
          regBytes[i] = (byte)(regBytes[i] >> 1);
        }
        string commandText = Encoding.UTF8.GetString(regBytes);

        Console.WriteLine($"   -> [Wire Sent] PREFIX: 1 (Handshake) | Command Data: '{commandText}' | Raw Wire Hex: {BitConverter.ToString(data)}");
      }
      else if (prefix == 0)
      {
        Console.WriteLine($"   -> [Wire Sent] PREFIX: 0 (Operation) | Payload Byte Count: {data.Length - 1} | Raw Wire Hex: {prefix}-{hexPayload}");
      }

      return Task.CompletedTask;
    }

    public Task SendTextAsync(string message, CancellationToken cancellationToken = default)
    {
      return Task.CompletedTask;
    }

    public Task StartListeningAsync(CancellationToken cancellationToken = default)
    {
      Console.WriteLine("   -> [Mock Socket] Started listening thread loop.");
      return Task.CompletedTask;
    }

    public void SimulateIncomingServerData(byte[] completePacket)
    {
      OnDataReceived?.Invoke(this, new ReadOnlyMemory<byte>(completePacket));
    }

    public void SimulateNetworkDrop()
    {
      IsConnected = false;
      Console.WriteLine("   -> [Mock Socket] ALERT: Connection lost violently!");
      OnDisconnected?.Invoke(this, "Connection reset by remote peer");
    }

    public void Dispose()
    {
      IsConnected = false;
    }
  }
}