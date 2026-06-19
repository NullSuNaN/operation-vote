using System.Collections.Concurrent;

namespace operation_vote.Server
{
  public class User(string Name, string ApiKey = "42", int VoteMultiplier = 1)
  {
    
    private readonly string name = Name;
    private string apiKey = ApiKey;
    private int voteMultiplier = VoteMultiplier;
    // private int maxInstances = int.MaxValue;
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
    private T SetProperty<T>(ref T field, T value)
    {
      infoLock.EnterWriteLock();
      try
      {
        T original = field;
        field=value;
        return original;
      }
      finally
      {
        infoLock.ExitWriteLock();
      }
    }
    public string Name => name;
    public string ApiKey {
      get => GetProperty(ref apiKey);
      set {
        string original = SetProperty(ref apiKey, value);
        OnApiKeyChange?.Invoke(this, (original, value));
      }
    }
    public int VoteMultiplier {
      get => GetProperty(ref voteMultiplier);
      set {
        int original = SetProperty(ref voteMultiplier, value);
        OnVoteMultiplierChange?.Invoke(this, (original, value));
      }
    }
    // public int MaxInstances {
    //   get => GetProperty(ref maxInstances);
    //   set => SetProperty(ref maxInstances, value);
    // }
    public void Set(string ApiKey, int VoteMultiplier)
    {
      int originalMultiplier=0;
      infoLock.EnterWriteLock();
      try
      {
        apiKey=ApiKey;
        originalMultiplier = voteMultiplier;
        voteMultiplier=VoteMultiplier;
      }
      finally
      {
        infoLock.ExitWriteLock();
        OnVoteMultiplierChange?.Invoke(this, (originalMultiplier, VoteMultiplier));
      }
    }
    public event EventHandler<(int Original, int New)>? OnVoteMultiplierChange;
    public event EventHandler<(string Original, string New)>? OnApiKeyChange;
    public readonly ReaderWriterLockSlim ConnectedClientsLock = new();
    public readonly ConcurrentDictionary<ClientInfo, object?> ConnectedClients = [];
  };
}