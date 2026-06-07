using System.Collections.Concurrent;
using System.Text;
using operation_vote.Shared;
using operation_vote.Server.Network;

namespace operation_vote.Server
{
  public class VotingServer
  {
    private readonly List<IServerChannel> _channels = [];
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
    public event EventHandler<ClientInfo>? OnClientCacheSynchronized;
    public event EventHandler<(ClientInfo Client, User User)>? OnClientAuthorized;
    public event EventHandler<(ClientInfo Client, string username)>? OnClientAuthorizeFailed;
    public event EventHandler<(ClientInfo Client, Operation ReceivedOperation)>? OnOperationReceived;
    public event EventHandler<(ClientInfo Client, Exception Error)>? OnServerErrorEncountered;
    private IUserContainer users;
    private readonly ReaderWriterLockSlim usersLock = new(LockRecursionPolicy.SupportsRecursion);
    public IUserContainer Users
    {
      set {
        usersLock.EnterWriteLock();
        try { users=value; }
        finally { usersLock.ExitWriteLock(); }
      }
    }
    /// <summary>
    /// Operate the users with read access
    /// </summary>
    /// <param name="action">the action, cannot use any upgradeable/write action.</param>
    public void UsersQuery(Action<IReadOnlyDictionary<string, User>> action)
    {
        usersLock.EnterReadLock();
        try { action(users.AsReadOnly()); }
        finally { usersLock.ExitReadLock(); }
    }
    /// <summary>
    /// Operate the users with read upgradeable access
    /// </summary>
    /// <param name="action">the action, can use <see cref="UsersOperate(Action{IUserContainer})"/>.</param>
    public void UsersQueryUpgradeable(Action<IReadOnlyDictionary<string, User>> action)
    {
        usersLock.EnterUpgradeableReadLock();
        try { action(users.AsReadOnly()); }
        finally { usersLock.ExitUpgradeableReadLock(); }
    }
    /// <summary>
    /// Operate the users with write access
    /// </summary>
    /// <param name="action">the action</param>
    public void UsersOperate(Action<IUserContainer> action)
    {
        usersLock.EnterWriteLock();
        try { action(users); }
        finally { usersLock.ExitWriteLock(); }
    }
    public readonly User unauthorizedUser;

    public VotingServer(IEnumerable<IServerChannel> functionalChannels, IEnumerable<Operation.OperationType> systemOperationTemplates, IUserContainer users)
    {
      ArgumentNullException.ThrowIfNull(functionalChannels, nameof(functionalChannels));
      _channels.AddRange(functionalChannels);
      this.users=users;
      unauthorizedUser=users.AnonymousUser;

      // Establish standard baseline operation templates matching workspace profiles
      _systemOperationTemplates = [.. systemOperationTemplates];

      // Dynamically bind to the network communication pipelines
      foreach (var channel in _channels)
      {
        channel.OnChannelClientConnected += (s, client) =>
        {
          client.User.ConnectedClients.TryAdd(client, null);
          _clients[client.ClientId] = client;
          OnClientConnected?.Invoke(this, client);
        };

        channel.OnChannelClientDisconnected += (s, e) =>
        {
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
      var runningTasks = _channels.Select(channel => channel.StartAsync(unauthorizedUser));
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
        switch(cmd)
        {
          case "INIT": // client starts a handshake
            using(var ms = new MemoryStream())
            using(var writer = new BinaryWriter(ms, Encoding.UTF8))
            {
              writer.Write("INIT");
              writer.Write($"VOTE-{ProtocolInfo.Version}");

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
              await SendDirectAsync(clientInfo, ms.ToArray());
              OnClientHandshakeCompleted?.Invoke(this, clientInfo);
              break;
            }
          case "REG": // client finished Synchronizing, initializes the session
            OnClientCacheSynchronized?.Invoke(this, clientInfo);
            break;
          case "AUTH": // client requests authentication
            User? user = null; // cache the user got from AuthenticateClientAsync
            string requestedUsername = "";
            string? username = await AuthenticationServer.AuthenticateClientAsync(async data =>
              {
                using var ms = new MemoryStream();
                using var writer = new BinaryWriter(ms, Encoding.UTF8);
                writer.Write("AUTH");
                writer.Write(data);
                writer.Flush();
                await clientInfo.Channel.SendToClientAsync(clientInfo, ms.ToArray());
                return await clientInfo.UserAuthenticationResult.Task;
              }, async username =>
              {
                requestedUsername=username;
                UsersQuery(users => users.TryGetValue(username, out user));

                return user?.ApiKey;
              }, async success =>
              {
                using var ms = new MemoryStream();
                using var writer = new BinaryWriter(ms, Encoding.UTF8);
                writer.Write("AUTH_RES");
                writer.Write(success);
                writer.Flush();
                await clientInfo.Channel.SendToClientAsync(clientInfo, ms.ToArray());
              }
            );
            if(user != null)
            {
              clientInfo.User = user;
              OnClientAuthorized?.Invoke(this, (clientInfo, user));
            }
            else
            {
              OnClientAuthorizeFailed?.Invoke(this, (clientInfo, requestedUsername));
            }
            break;
          case "AUTH_RES": // client sends authentication result
            int length = reader.Read7BitEncodedInt();
            clientInfo.UserAuthenticationResult.SetResult(reader.ReadBytes(length));
            break;
        }
      }
      else
      {
        // If the user sends an operations, there will not be any response to save time
        var op = Operation.Deserialize(reader, _systemOperationTemplates);
        if(op != null)
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
      writer.Write("END");
      writer.Flush();
      foreach (var channel in _channels)
      {
        await channel.BroadcastAsync(ms.ToArray());
      }
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