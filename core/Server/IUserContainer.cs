namespace operation_vote.Server
{
  public interface IUserContainer : IDictionary<string, User>
  {
    public User AnonymousUser { get; }
    public event EventHandler<User>? OnUserRegistered;
    public event EventHandler<User>? OnUserDeleted;
  }
}