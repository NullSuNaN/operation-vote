using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace operation_vote.Server.Network
{
  [UnsupportedOSPlatform("browser")]
  public class ConcurrentChannelWrapper<T> : IConcurrentServerChannel
    where T : IServerChannel
  {
    private readonly T _underlyingChannel;

    // Direct synchronization locks mapping the outer ClientInfo handles
    private readonly ConcurrentDictionary<ClientInfo, SemaphoreSlim> _clientLocks = new();

    // Bi-directional mapping tracking infrastructure using exactly ClientInfo for keys and values
    private readonly ConcurrentDictionary<ClientInfo, ClientInfo> _outerToInner = new();
    private readonly ConcurrentDictionary<ClientInfo, ClientInfo> _innerToOuter = new();

    public ConcurrentChannelWrapper(T underlyingChannel)
    {
      _underlyingChannel = underlyingChannel ?? throw new ArgumentNullException(nameof(underlyingChannel));

      _underlyingChannel.OnChannelClientConnected += HandleClientConnected;
      _underlyingChannel.OnChannelDataReceived += HandleDataReceived;
      _underlyingChannel.OnChannelDataSent += HandleDataSent;
      _underlyingChannel.OnChannelClientDisconnected += HandleClientDisconnected;
    }

    public event EventHandler<ClientInfo>? OnChannelClientConnected;
    public event EventHandler<(ClientInfo Client, string Reason)>? OnChannelClientDisconnected;
    public event EventHandler<(ClientInfo Client, byte[] Payload)>? OnChannelDataReceived;
    public event EventHandler<(ClientInfo Client, byte[] Payload)>? OnChannelDataSent;

    public Task StartAsync(User unauthorizedUser) => _underlyingChannel.StartAsync(unauthorizedUser);

    public async Task SendToClientAsync(ClientInfo client, byte[] data)
    {
      if (client == null) return;

      // Transform from Outer to Inner context to route down to the underlying raw channel securely
      if (_outerToInner.TryGetValue(client, out var innerClient))
      {
        var semaphore = _clientLocks.GetOrAdd(client, _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
          await _underlyingChannel.SendToClientAsync(innerClient, data).ConfigureAwait(false);
        }
        finally
        {
          semaphore.Release();
        }
      }
    }
    public async Task ResetAsync(ClientInfo client)
    {
      if (client == null) return;

      // Transform from Outer to Inner context to route down to the underlying raw channel securely
      if (_outerToInner.TryGetValue(client, out var innerClient))
      {
        var semaphore = _clientLocks.GetOrAdd(client, _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
          await _underlyingChannel.ResetAsync(innerClient).ConfigureAwait(false);
        }
        finally
        {
          semaphore.Release();
        }
      }
    }

    public async Task BroadcastAsync(byte[] data)
    {
      if (data == null || data.Length == 0) return;

      // Gather all currently tracked outer client contexts
      var activeOuterClients = _outerToInner.Keys;

      // Broadcast to every client concurrently while fully respecting their thread-locks
      var broadcastTasks = new List<Task>();
      foreach (var outerClient in activeOuterClients)
      {
        broadcastTasks.Add(SendToClientAsync(outerClient, data));
      }

      await Task.WhenAll(broadcastTasks).ConfigureAwait(false);
    }

    public void Stop()
    {
      _underlyingChannel.Stop();
      _outerToInner.Clear();
      _innerToOuter.Clear();
      foreach (var sem in _clientLocks.Values) sem.Dispose();
      _clientLocks.Clear();
    }

    public void Dispose()
    {
      Stop();
      _underlyingChannel.Dispose();
      GC.SuppressFinalize(this);
    }

    private void HandleClientConnected(object? sender, ClientInfo innerClient)
    {
      // Wrap and allocate the fresh Outer ClientInfo instance
      var outerClient = new ClientInfo(this, innerClient.ClientId, innerClient.User, innerClient.UserAuthenticationResult);

      // Store references into the bi-directional dictionaries mapping ClientInfo directly
      _outerToInner[outerClient] = innerClient;
      _innerToOuter[innerClient] = outerClient;

      // Expose the thread-safe outer wrapper instance up to the application ecosystem
      OnChannelClientConnected?.Invoke(this, outerClient);
    }

    private void HandleDataReceived(object? sender, (ClientInfo Client, byte[] Payload) e)
    {
      // Transform from Inner to Outer context so downstream handlers receive the safe instance mapping
      if (_innerToOuter.TryGetValue(e.Client, out var outerClient))
      {
        OnChannelDataReceived?.Invoke(this, (outerClient, e.Payload));
      }
      else
      {
        OnChannelDataReceived?.Invoke(this, e);
      }
    }

    private void HandleDataSent(object? sender, (ClientInfo Client, byte[] Payload) e)
    {
      // Transform from Inner to Outer context so downstream handlers receive the safe instance mapping
      if (_innerToOuter.TryGetValue(e.Client, out var outerClient))
      {
        OnChannelDataSent?.Invoke(this, (outerClient, e.Payload));
      }
      else
      {
        OnChannelDataSent?.Invoke(this, e);
      }
    }

    private void HandleClientDisconnected(object? sender, (ClientInfo Client, string Reason) e)
    {
      // Transform from Inner to Outer context to correctly fire the event and clean up pools
      if (_innerToOuter.TryRemove(e.Client, out var outerClient))
      {
        _outerToInner.TryRemove(outerClient, out _);

        if (_clientLocks.TryRemove(outerClient, out var sem))
        {
          sem.Dispose();
        }

        OnChannelClientDisconnected?.Invoke(this, (outerClient, e.Reason));
      }
      else
      {
        OnChannelClientDisconnected?.Invoke(this, e);
      }
    }
  }
}