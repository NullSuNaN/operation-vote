namespace operation_vote.Shared
{
  public static class ReaderWriterLockSlimExtensions
  {
  
    public static ReaderWriterLockSlimToken EnterReadLockAsToken(this ReaderWriterLockSlim @lock)
    {
      @lock.EnterReadLock();
      return new(@lock, ReaderWriterLockSlimToken.LockType.Read);
    }
    public static ReaderWriterLockSlimToken EnterUpgradeableReadLockAsToken(this ReaderWriterLockSlim @lock)
    {
      @lock.EnterUpgradeableReadLock();
      return new(@lock, ReaderWriterLockSlimToken.LockType.ReadUpgradeable);
    }
    public static ReaderWriterLockSlimToken EnterWriteLockAsToken(this ReaderWriterLockSlim @lock)
    {
      @lock.EnterWriteLock();
      return new(@lock, ReaderWriterLockSlimToken.LockType.Write);
    }
  }
  public class ReaderWriterLockSlimToken : IDisposable
  {
    public void Dispose()
    {
      GC.SuppressFinalize(this);
      if(isDisposed)
        return;
      isDisposed=true;
      switch(lockType)
      {
        case LockType.Read:
          targetLock.ExitReadLock();
          break;
        case LockType.ReadUpgradeable:
          targetLock.ExitUpgradeableReadLock();
          break;
        case LockType.Write:
          targetLock.ExitWriteLock();
          break;
      }
    }

    private readonly ReaderWriterLockSlim targetLock;
    private readonly LockType lockType;
    private bool isDisposed = false;
    internal enum LockType
    {
      Read,
      ReadUpgradeable,
      Write
    }
    internal ReaderWriterLockSlimToken(ReaderWriterLockSlim targetLock, LockType lockType)
    {
      this.targetLock=targetLock;
      this.lockType=lockType;
    }
  }
}