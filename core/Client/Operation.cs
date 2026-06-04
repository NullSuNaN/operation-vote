using System;
using System.IO;
using System.Text;
using System.Threading;
using operation_vote.Shared;

namespace operation_vote.Client
{
  public class Operation : IDisposable
  {
    public class OperationType
    {
      public readonly VotingEnv Env;
      public readonly long Id;
      public readonly byte[] Instructions;

      internal OperationType(byte[] instructions, long id, VotingEnv env)
      {
        Instructions = instructions;
        Id = id;
        Env = env;
      }
    }

    public readonly OperationType Type;
    public readonly long Id;
    public readonly VoteType VoteType;

    private byte[] _stateBytes;
    private int _isDisposedState; // 0 = false, 1 = true
    private readonly ReaderWriterLockSlim _stateLock = new();

    public bool IsDisposed => Volatile.Read(ref _isDisposedState) == 1;

    public byte[] StateBytes
    {
      get
      {
        _stateLock.EnterReadLock();
        try
        {
          // Return a copy to ensure safe processing across separate thread tasks
          return (byte[])_stateBytes.Clone();
        }
        finally
        {
          _stateLock.ExitReadLock();
        }
      }
      set
      {
        _stateLock.EnterWriteLock();
        try
        {
          _stateBytes = value ?? throw new ArgumentNullException(nameof(value));
        }
        finally
        {
          _stateLock.ExitWriteLock();
        }
      }
    }

    public Operation(Operation.OperationType type, VoteType voteType, byte[] stateBytes)
    {
      Type = type ?? throw new ArgumentNullException(nameof(type));
      Id = type.Env.NewId;
      VoteType = voteType;
      _stateBytes = stateBytes ?? throw new ArgumentNullException(nameof(stateBytes));
    }

    public byte[] ToByteArray()
    {
      using var ms = new MemoryStream();
      using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

      writer.Write(Type.Id);
      writer.Write((byte)VoteType);

      _stateLock.EnterReadLock();
      try
      {
        writer.Write(_stateBytes.Length);
        writer.Write(_stateBytes);
      }
      finally
      {
        _stateLock.ExitReadLock();
      }

      writer.Flush();
      return ms.ToArray();
    }

    public void Dispose()
    {
      Dispose(true);
      GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
      if (Interlocked.Exchange(ref _isDisposedState, 1) == 0)
      {
        if (disposing)
        {
          _stateLock.EnterWriteLock();
          try
          {
            _stateBytes = [];
          }
          finally
          {
            _stateLock.ExitWriteLock();
          }
          _stateLock.Dispose();
        }
      }
    }
  }
}