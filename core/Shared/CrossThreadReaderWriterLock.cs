using System.Diagnostics;

namespace operation_vote.Shared
{
  public class CrossThreadReaderWriterLock
  {
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private int _readerCount = 0;

    // --- Writer Methods ---
    public void EnterWriteLock()
    {
      // Block both new writers and the reader pool
      _writeLock.Wait();
    }

    public void ExitWriteLock()
    {
      // Can be called by a completely different thread
      _writeLock.Release();
    }

    // --- Reader Methods ---
    public void EnterReadLock()
    {
      _readLock.Wait();
      try
      {
        _readerCount++;
        if (_readerCount == 1)
        {
          // The first reader blocks writers
          _writeLock.Wait();
        }
      }
      finally
      {
        _readLock.Release();
      }
    }

    public void ExitReadLock()
    {
      _readLock.Wait();
      try
      {
        _readerCount--;
        if (_readerCount == 0)
        {
          // The last reader releases the writers
          _writeLock.Release();
        }
      }
      finally
      {
        _readLock.Release();
      }
    }
    public CrossThreadLockToken EnterReadLockAsToken()
    {
      EnterReadLock();
      return new CrossThreadLockToken(this, CrossThreadLockToken.LockType.Read);
    }

    public CrossThreadLockToken EnterWriteLockAsToken()
    {
      EnterWriteLock();
      return new CrossThreadLockToken(this, CrossThreadLockToken.LockType.Write);
    }

    public class CrossThreadLockToken : IDisposable
    {
      private readonly CrossThreadReaderWriterLock _targetLock;
      private readonly LockType _lockType;
      private bool _isDisposed = false;

      internal enum LockType { Read, Write }

      internal CrossThreadLockToken(CrossThreadReaderWriterLock targetLock, LockType lockType)
      {
        _targetLock = targetLock;
        _lockType = lockType;
      }

      public void Dispose()
      {
        GC.SuppressFinalize(this);

        if (_isDisposed)
          return;

        _isDisposed = true;

        switch (_lockType)
        {
          case LockType.Read:
            _targetLock.ExitReadLock();
            break;
          case LockType.Write:
            _targetLock.ExitWriteLock();
            break;
        }
      }

      ~CrossThreadLockToken()
      {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"A {typeof(CrossThreadLockToken)} was leaked without being disposed!\a");
        Console.ForegroundColor = ConsoleColor.White;
        Debugger.Break();
      }
    }
  }
}