using System.Collections.Concurrent;
using operation_vote.Shared.Extensions;

namespace operation_vote.Server
{
  public class User(string Name, string ApiKey = "42", int VoteMultiplier = 1, int? SessionsLimit = null)
  {

    private readonly string name = Name;
    private string apiKey = ApiKey;
    private int voteMultiplier = VoteMultiplier;
    private int? sessionsLimit = SessionsLimit;
    public readonly ReaderWriterLockSlim infoLock = new(LockRecursionPolicy.SupportsRecursion);
    private T GetProperty<T>(ref T field)
    {
      infoLock.EnterReadLock();
      try
      {
        return field;
      }
      finally
      {
        infoLock.ExitReadLock();
      }
    }
    private T SetProperty<T>(ref T field, T value, EventHandler<(T Original, T New)>? eventHandler)
      where T : IEquatable<T>
    {
      infoLock.EnterWriteLock();
      try
      {
        T original = Interlocked.Exchange(ref field, value);
        if (!original.Equals(value))
          eventHandler?.Invoke(this, (original, value));
        return original;
      }
      finally
      {
        infoLock.ExitWriteLock();
      }
    }
    private T? SetPropertyNullable<T>(ref T? field, T? value, EventHandler<(T? Original, T? New)>? eventHandler)
      where T : struct, IEquatable<T>
    {
      infoLock.EnterWriteLock();
      try
      {
        T? original = Interlocked.Exchange(ref field, value);
        if (!original?.Equals(value) ?? (original is null && value is not null))
          eventHandler?.Invoke(this, (original, value));
        return original;
      }
      finally
      {
        infoLock.ExitWriteLock();
      }
    }
    public string Name => name;
    public string ApiKey
    {
      get => GetProperty(ref apiKey);
      set => SetProperty(ref apiKey, value, OnApiKeyChangeLocked);
    }
    public int VoteMultiplier
    {
      get => GetProperty(ref voteMultiplier);
      set => SetProperty(ref voteMultiplier, value, OnVoteMultiplierChangeLocked);
    }
    public int? SessionsLimit
    {
      get => GetProperty(ref sessionsLimit);
      set => SetPropertyNullable<int>(ref sessionsLimit, value, OnSessionsLimitChangeLocked);
    }
    public void Set(string ApiKey, int VoteMultiplier, int? SessionsLimit)
    {
      string originalApiKey = "";
      int originalMultiplier = 0;
      int? originalSessionsLimit = null;
      infoLock.EnterWriteLock();
      try
      {
        originalApiKey = apiKey;
        apiKey = ApiKey;
        originalMultiplier = voteMultiplier;
        voteMultiplier = VoteMultiplier;
        originalSessionsLimit = sessionsLimit;
        sessionsLimit = SessionsLimit;
      }
      finally
      {
        if (originalApiKey != ApiKey)
          OnApiKeyChangeLocked?.Invoke(this, (originalApiKey, ApiKey));
        if (originalMultiplier != VoteMultiplier)
          OnVoteMultiplierChangeLocked?.Invoke(this, (originalMultiplier, VoteMultiplier));
        if (originalSessionsLimit != SessionsLimit)
          OnSessionsLimitChangeLocked?.Invoke(this, (originalSessionsLimit, SessionsLimit));
        infoLock.ExitWriteLock();
      }
    }
    public event EventHandler<(int Original, int New)>? OnVoteMultiplierChangeLocked;
    public event EventHandler<(string Original, string New)>? OnApiKeyChangeLocked;
    public event EventHandler<(int? Original, int? New)>? OnSessionsLimitChangeLocked;
    public readonly ReaderWriterLockSlim ConnectedClientsLock = new();
    public readonly ConcurrentDictionary<ClientInfo, object?> ConnectedClients = [];

    public bool TryAddClient(ClientInfo client)
    {
      using (client.userLock.EnterUpgradeableReadLockAsToken())
      {
        if (ReferenceEquals(client.User, this))
          return true;
        using (client.userLock.EnterWriteLockAsToken())
        using (infoLock.EnterReadLockAsToken())
        {
          if (ConnectedClients.Count + 1 > sessionsLimit) return false;
          client.User = this;
          return true;
        }
      }
    }
  };
}