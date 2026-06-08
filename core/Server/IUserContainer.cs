namespace operation_vote.Server
{
  /// <summary>
  /// A user container.
  /// Anonymous should be stored separately and cannot be access with <see cref="IDictionary{TKey, TValue}"> methods.
  /// </summary>
  public interface IUserContainer : IDictionary<string, User>
  {
    public User AnonymousUser { get; }
    public event EventHandler<User>? OnUserRegistered;
    public event EventHandler<User>? OnUserDeleted;
  }
}