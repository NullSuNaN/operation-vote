using System.Collections.Concurrent;
using System.Text;
using operation_vote.Shared;
using operation_vote.Server.Network;

namespace operation_vote.Server
{
  public class VotingServer
  {
    private readonly List<IServerChannel> _channels = [];
    private readonly ConcurrentDictionary<Guid, IServerChannel> _clientChannelRoutingMap = new();

    // Expose the configured system operation templates as a read-only list for the API consumer
    private readonly List<Operation.OperationType> _systemOperationTemplates = [];
    public IReadOnlyList<Operation.OperationType> SystemOperationTemplates => _systemOperationTemplates;

    // --- API EVENTS ---
    public event EventHandler<Guid>? OnClientConnected;
    public event EventHandler<(Guid ClientId, string Reason)>? OnClientDisconnected;
    public event EventHandler<Guid>? OnClientHandshakeCompleted;
    public event EventHandler<Guid>? OnClientCacheSynchronized;
    public event EventHandler<(Guid ClientId, Operation ReceivedOperation)>? OnOperationReceived;
    public event EventHandler<(Guid ClientId, Exception Error)>? OnServerErrorEncountered;

    public VotingServer(IEnumerable<IServerChannel> functionalChannels, IEnumerable<Operation.OperationType> systemOperationTemplates)
    {
      if (functionalChannels == null) throw new ArgumentNullException(nameof(functionalChannels));
      _channels.AddRange(functionalChannels);

      // Establish standard baseline operation templates matching workspace profiles
      _systemOperationTemplates = [.. systemOperationTemplates];

      // Dynamically bind to the network communication pipelines
      foreach (var channel in _channels)
      {
        channel.OnChannelClientConnected += (s, clientId) =>
        {
          _clientChannelRoutingMap[clientId] = channel;
          OnClientConnected?.Invoke(this, clientId);
        };

        channel.OnChannelClientDisconnected += (s, e) =>
        {
          _clientChannelRoutingMap.TryRemove(e.ClientId, out _);
          OnClientDisconnected?.Invoke(this, e);
        };

        channel.OnChannelDataReceived += async (s, e) =>
        {
          try
          {
            await ProcessIncomingFrameAsync(e.ClientId, e.Payload);
          }
          catch (Exception ex)
          {
            OnServerErrorEncountered?.Invoke(this, (e.ClientId, ex));
          }
        };
      }
    }

    public async Task StartAsync()
    {
      var runningTasks = _channels.Select(channel => channel.StartAsync());
      await Task.WhenAll(runningTasks);
    }

    private async Task ProcessIncomingFrameAsync(Guid clientId, byte[] packetBytes)
    {
      // Extract the data using BitPacker.
      // Packet bytes arriving here are stripped of any raw TCP 4-byte length prefixes.
      var (prefix, payload) = BitPacker.Unpack(packetBytes);

      if (prefix == 1) // Handshake Layer Frame Identifier
      {
        string cmd = Encoding.UTF8.GetString(payload).TrimEnd('\0');
        if (cmd == "INIT")
        {
          using var ms = new MemoryStream();
          using var writer = new BinaryWriter(ms, Encoding.UTF8);

          writer.Write((byte)'V');
          writer.Write("VOTE-1.1");

          writer.Write(_systemOperationTemplates.Count);
          foreach (var template in _systemOperationTemplates)
          {
            writer.Write(template.Instructions.Length);
            writer.Write(template.Instructions);
            writer.Write(template.Id);
          }
          writer.Flush();

          // Pack the message using BitPacker before sending.
          // The underlying TcpServerChannel will prepend the necessary length headers automatically.
          byte[] packedFrame = BitPacker.Pack(startingBit: 1, ms.ToArray());
          await SendDirectAsync(clientId, packedFrame);
          OnClientHandshakeCompleted?.Invoke(this, clientId);
        }
        else if (cmd == "REG")
        {
          OnClientCacheSynchronized?.Invoke(this, clientId);
        }
      }
      else if (prefix == 0) // Application Operation Frame Identifier
      {
        Operation? clientOperation = Operation.Deserialize(payload, _systemOperationTemplates);
        if(clientOperation != null)
        {
          OnOperationReceived?.Invoke(this, (clientId, clientOperation));

          // Serialize acknowledgement update loop states
          byte[] rawResponseBytes = clientOperation.ToByteArray();
          byte[] packedAckFrame = BitPacker.Pack(startingBit: 0, rawResponseBytes);
          await SendDirectAsync(clientId, packedAckFrame);
        }
      }
    }

    private async Task SendDirectAsync(Guid clientId, byte[] frameBytes)
    {
      if (_clientChannelRoutingMap.TryGetValue(clientId, out var channel))
      {
        await channel.SendToClientAsync(clientId, frameBytes);
      }
    }

    public async Task BroadcastShutdownSignalAsync()
    {
      byte[] endPayload = Encoding.UTF8.GetBytes("END");
      byte[] packedEndFrame = BitPacker.Pack(startingBit: 1, endPayload);

      foreach (var channel in _channels)
      {
        await channel.BroadcastAsync(packedEndFrame);
      }
    }

    public void Stop()
    {
      foreach (var channel in _channels)
      {
        channel.Stop();
      }
      _clientChannelRoutingMap.Clear();
    }
  }
}