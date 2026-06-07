using operation_vote.Server.Network;

namespace operation_vote.Server
{
  /// <summary>
  /// 
  /// </summary>
  /// <param name="Channel">The request channel</param>
  /// <param name="User">User data, null if it is not authenticated.</param>
  public class ClientInfo(IServerChannel Channel, Guid ClientId, User User)
  {
    private IServerChannel channel = Channel;
    private Guid clientId = ClientId;
    private User user = User;
    private TaskCompletionSource<byte[]> userAuthenticationResult = new();
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
    private void SetProperty<T>(ref T field, T value)
    {
      infoLock.EnterWriteLock();
      try
      {
        field=value;
      }
      finally
      {
        infoLock.ExitWriteLock();
      }
    }
    public IServerChannel Channel {
      get => GetProperty(ref channel);
      set => SetProperty(ref channel, value);
    }
    public Guid ClientId {
      get => GetProperty(ref clientId);
      set => SetProperty(ref clientId, value);
    }
    public User User {
      get => GetProperty(ref user);
      set
      {
        infoLock.EnterWriteLock();
        try
        {
          user.ConnectedClients.TryRemove(this, out _);
          value.ConnectedClients.TryAdd(this, null);
          user=value;
        }
        finally
        {
          infoLock.ExitWriteLock();
        }
      }
    }
    public TaskCompletionSource<byte[]> UserAuthenticationResult => GetProperty(ref userAuthenticationResult);
  }
}