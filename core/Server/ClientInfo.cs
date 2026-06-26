using operation_vote.Server.Network;
using operation_vote.Shared.Extensions;

namespace operation_vote.Server
{
  /// <summary>
  /// 
  /// </summary>
  /// <param name="Channel">The request channel</param>
  /// <param name="User">User data, null if it is not authenticated.</param>
  public class ClientInfo(IServerChannel Channel, Guid ClientId, User User, TaskCompletionSource<byte[]> UserAuthenticationResult = null!)
  {
    public IServerChannel Channel = Channel;
    public Guid ClientId = ClientId;
    private User user = User;
    private TaskCompletionSource<byte[]> userAuthenticationResult = UserAuthenticationResult ?? new();
    public readonly ReaderWriterLockSlim userLock = new(LockRecursionPolicy.SupportsRecursion);
    public bool Initialized { get; private set; } = false;
    private T GetProperty<T>(ref T field)
    {
      using(userLock.EnterReadLockAsToken())
        return field;
    }
    private void SetProperty<T>(ref T field, T value)
    {
      using(userLock.EnterWriteLockAsToken())
        field = value;
    }
    public User User
    {
      get => GetProperty(ref user);
      set
      {
        userLock.EnterWriteLock();
        try
        {
          user.ConnectedClients.TryRemove(this, out _);
          value.ConnectedClients.TryAdd(this, null);
          user = value;
        }
        finally
        {
          userLock.ExitWriteLock();
        }
      }
    }
    public IServerChannel ExchangeChannel(IServerChannel value) => Interlocked.Exchange(ref Channel, value);
    public Guid ExchangeClientId(Guid value) => Interlocked.Exchange(ref ClientId, value);
    public User ExchangeUser(User value)
    {
      using var __ = userLock.EnterWriteLockAsToken();
      if(user == value) return user;
      user.ConnectedClients.TryRemove(this, out _);
      value.ConnectedClients.TryAdd(this, null);
      return Interlocked.Exchange(ref user, value);
    }
    public TaskCompletionSource<byte[]> UserAuthenticationResult => GetProperty(ref userAuthenticationResult);

    public void Initialize() =>
      Initialized = true;
  }
}