using System.Collections.Concurrent;
using System.Text;
using operation_vote.Shared;
using operation_vote.Server.Network;

namespace operation_vote.Server
{
  public class VotingServer
  {
    private readonly List<IConcurrentServerChannel> _channels = [];
    private readonly ConcurrentDictionary<Guid, ClientInfo> _clients = new();

    private readonly List<Operation.OperationType> _systemOperationTemplates = [];
    /// <summary>
    /// Expose the configured system operation templates as a read-only list for the API consumer
    /// </summary>
    public IReadOnlyList<Operation.OperationType> SystemOperationTemplates => _systemOperationTemplates;

    // --- API EVENTS ---
    public event EventHandler<ClientInfo>? OnClientConnected;
    public event EventHandler<(ClientInfo ClientId, string Reason)>? OnClientDisconnected;
    public event EventHandler<ClientInfo>? OnClientHandshakeCompleted;
    public event EventHandler<ClientInfo>? OnClientJoined;
    public event EventHandler<(ClientInfo Client, User User)>? OnClientAuthorized;
    public event EventHandler<(ClientInfo Client, string username)>? OnClientAuthorizeFailed;
    public event EventHandler<(ClientInfo Client, Operation ReceivedOperation)>? OnOperationReceived;
    public event EventHandler<(ClientInfo Client, Exception Error)>? OnServerErrorEncountered;
    public event EventHandler<User>? OnUserRegistered;
    public event EventHandler<(User User, IEnumerable<ClientInfo> OriginalClients)>? OnUserDeleted;
    public readonly IUserContainer Users;
    public User UnauthorizedUser => Users.AnonymousUser;

    public VotingServer(IEnumerable<IConcurrentServerChannel> functionalChannels, IEnumerable<Operation.OperationType> systemOperationTemplates, IUserContainer Users)
    {
      _channels.AddRange(functionalChannels);
      this.Users = Users;
      Users.OnUserRegistered += HandleUserRegistered;
      Users.OnUserDeleted += HandleUserDeleted;
      foreach (var user in Users)
      {
        user.Value.OnVoteMultiplierChange += HandleUserVoteMultiplierChange;
      }
      UnauthorizedUser.OnVoteMultiplierChange += HandleUserVoteMultiplierChange;
      // Users.OnUserDeleted;

      // Establish standard baseline operation templates matching workspace profiles
      _systemOperationTemplates = [.. systemOperationTemplates];

      // Dynamically bind to the network communication pipelines
      foreach (var channel in _channels)
      {
        channel.OnChannelClientConnected += (s, client) =>
        {
          using (client.User.ConnectedClientsLock.EnterWriteLockAsToken())
            client.User.ConnectedClients.TryAdd(client, null);
          _clients[client.ClientId] = client;
          OnClientConnected?.Invoke(this, client);
        };

        channel.OnChannelClientDisconnected += (s, e) =>
        {
          using (e.Client.User.ConnectedClientsLock.EnterWriteLockAsToken())
            e.Client.User.ConnectedClients.TryRemove(e.Client, out _);
          _clients.TryRemove(e.Client.ClientId, out _);
          OnClientDisconnected?.Invoke(this, e);
        };

        channel.OnChannelDataReceived += async (s, e) =>
        {
          try
          {
            await ProcessIncomingFrameAsync(e.Client, e.Payload);
          }
          catch (Exception ex)
          {
            OnServerErrorEncountered?.Invoke(this, (e.Client, ex));
          }
        };
      }
    }

    public async Task StartAsync()
    {
      var runningTasks = _channels.Select(channel => channel.StartAsync(UnauthorizedUser));
      await Task.WhenAll(runningTasks);
    }

    private async Task ProcessIncomingFrameAsync(ClientInfo clientInfo, byte[] packetBytes)
    {
      using var rms = new MemoryStream(packetBytes);
      using var reader = new BinaryReader(rms, Encoding.UTF8);
      bool isCommand = reader.ReadBoolean();

      if (isCommand) // Handshake Layer Frame Identifier
      {
        string cmd = reader.ReadString();
        switch (cmd)
        {
          case ProtocolInfo.ClientCommands.InitializeCommand: // client starts a handshake
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms, Encoding.UTF8))
            {
              writer.Write(ProtocolInfo.ServerCommands.InitializeCommand);
              writer.Write($"VOTE-{ProtocolInfo.Version}");

              writer.Write(_systemOperationTemplates.Count);
              foreach (var template in _systemOperationTemplates)
              {
                writer.Write(template.Instructions.Length);
                writer.Write(template.Instructions);
                writer.Write(template.Id);
              }
              writer.Flush();
              await SendDirectAsync(clientInfo, ms.ToArray());
              OnClientHandshakeCompleted?.Invoke(this, clientInfo);
            }
            int multiplier = UnauthorizedUser.VoteMultiplier;
            if (multiplier != ProtocolInfo.ClientDefaultVoteMultiplier)
              using (var ms = new MemoryStream())
              using (var writer = new BinaryWriter(ms, Encoding.UTF8))
              {
                writer.Write(ProtocolInfo.ServerCommands.UpdateStatusCommand);
                writer.Write("MUL");
                writer.Write7BitEncodedInt(multiplier);
                writer.Flush();
                await SendDirectAsync(clientInfo, ms.ToArray());
              }
            break;
          case ProtocolInfo.ClientCommands.RegisterInstanceCommand: // client finished Synchronizing, initializes the session
            clientInfo.Initialize();
            OnClientJoined?.Invoke(this, clientInfo);
            break;
          case ProtocolInfo.ClientCommands.AuthenticateRequestCommand: // client requests authentication
            User? user = null; // cache the user got from AuthenticateClientAsync
            string requestedUsername = "";
            string? username = await AuthenticationServer.AuthenticateClientAsync(async data =>
              {
                using var ms = new MemoryStream();
                using var writer = new BinaryWriter(ms, Encoding.UTF8);
                writer.Write(ProtocolInfo.ServerCommands.AuthenticateChallengeCommand);
                writer.Write(data);
                writer.Flush();
                await clientInfo.Channel.SendToClientAsync(clientInfo, ms.ToArray());
                return await clientInfo.UserAuthenticationResult.Task;
              }, async username =>
              {
                requestedUsername = username;
                Users.TryGetValue(username, out user);

                return user?.ApiKey;
              }, async success =>
              {
                using var ms = new MemoryStream();
                using var writer = new BinaryWriter(ms, Encoding.UTF8);
                writer.Write(ProtocolInfo.ServerCommands.AuthenticateResultCommand);
                writer.Write(success);
                writer.Flush();
                await clientInfo.Channel.SendToClientAsync(clientInfo, ms.ToArray());
              }
            );
            if (user != null)
            {
              clientInfo.User = user;
              await SendMultiplierUpdateAsync(user);
              OnClientAuthorized?.Invoke(this, (clientInfo, user));
            }
            else
            {
              OnClientAuthorizeFailed?.Invoke(this, (clientInfo, requestedUsername));
            }
            break;
          case ProtocolInfo.ClientCommands.AuthenticateResultCommand: // client sends authentication result
            int length = reader.Read7BitEncodedInt();
            clientInfo.UserAuthenticationResult.SetResult(reader.ReadBytes(length));
            break;
        }
      }
      else
      {
        // If the user sends an operation, there will not be any response to save time
        var op = Operation.Deserialize(reader, _systemOperationTemplates);
        if (op != null)
          OnOperationReceived?.Invoke(this, (clientInfo, op));
      }
    }

#pragma warning disable CA1822
    private async Task SendDirectAsync(ClientInfo client, byte[] frameBytes)
    {
      await client.Channel.SendToClientAsync(client, frameBytes);
    }
#pragma warning restore CA1822

    public async Task BroadcastShutdownSignalAsync()
    {
      using var ms = new MemoryStream();
      using var writer = new BinaryWriter(ms, Encoding.UTF8);
      writer.Write(ProtocolInfo.ServerCommands.EndSessionCommand);
      writer.Flush();
      foreach (var channel in _channels)
      {
        await channel.BroadcastAsync(ms.ToArray());
      }
    }

    private void HandleUserVoteMultiplierChange(object? sender, (int Original, int New) e)
      => _ = SendMultiplierUpdateAsync((User?)sender ?? null!);
    private async Task SendMultiplierUpdateAsync(User user)
    {
      Console.WriteLine($"The multiplier of user {user.Name} is set to {user.VoteMultiplier}.");
      List<ClientInfo> clients;
      using (user.ConnectedClientsLock.EnterReadLockAsToken())
        clients = user.ConnectedClients.Keys.ToList();
      foreach (var item in clients)
      {
        // Check if client's User property matches the target user before sending
        // This avoids sending to clients that are still associated with a different user
        if (!ReferenceEquals(item.User, user))
          continue;

        try
        {
          using var ms = new MemoryStream();
          using var writer = new BinaryWriter(ms, Encoding.UTF8);
          writer.Write(ProtocolInfo.ServerCommands.UpdateStatusCommand);
          writer.Write("MUL");
          writer.Write7BitEncodedInt(user.VoteMultiplier);

          await SendDirectAsync(item, ms.ToArray());
        }
        catch (Exception ex)
        {
          Console.WriteLine($"Failed to send multiplier update to client {item.ClientId}: {ex.Message}");
          // Don't rethrow - allow other clients to receive the update
        }
      }
    }
    private async void HandleUserDeleted(object? _, User user)
    {
      user.OnVoteMultiplierChange -= HandleUserVoteMultiplierChange;
      IEnumerable<ClientInfo> clients;
      using (user.ConnectedClientsLock.EnterWriteLockAsToken())
        clients = user.ConnectedClients.Select(client =>
        {
          client.Key.User = UnauthorizedUser;
          return client.Key;
        });
        Console.WriteLine($"User {user.Name} is deleted.");
      foreach (var item in clients)
      {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8);
        writer.Write(ProtocolInfo.ServerCommands.UpdateStatusCommand);
        writer.Write("ACO");
        writer.Write(false);
        await SendDirectAsync(item, ms.ToArray());
      }
      OnUserDeleted?.Invoke(this, (user, clients));
    }
    private async void HandleUserRegistered(object? _, User user)
    {
      Console.WriteLine($"User {user.Name} is registered.");
      user.OnVoteMultiplierChange += HandleUserVoteMultiplierChange;
      OnUserRegistered?.Invoke(this, user);
    }

    public void Stop()
    {
      foreach (var channel in _channels)
      {
        channel.Stop();
      }
      _clients.Clear();
    }
  }
}