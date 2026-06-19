using System.Runtime.CompilerServices;
using System.Text;
using operation_vote.Shared;

namespace operation_vote.Client
{
  public class Operation(Operation.OperationType type, VoteType voteType, byte[] stateBytes) : IDisposable
  {
    /// <remarks>
    /// Access within <see cref="OpTypeLock" />
    /// </remarks>
    public class OperationType : IDisposable
    {
      public readonly VotingEnv Env;
      public readonly long Id;
      public readonly byte[] Instructions;
      public bool IsDisposed { get; private set; } = false;
      public ReaderWriterLockSlim OpTypeLock = new();

      internal OperationType(byte[] instructions, long id, VotingEnv env)
      {
        Instructions = instructions;
        Id = id;
        Env = env;
      }

      public void Dispose()
      {
        GC.SuppressFinalize(this);
        IsDisposed = true;
      }
    }

    public readonly OperationType Type = type ?? throw new ArgumentNullException(nameof(type));
    public readonly long Id = type.Env.NewId;
    public readonly VoteType VoteType = voteType;

    private byte[] _stateBytes = stateBytes ?? throw new ArgumentNullException(nameof(stateBytes));
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

    /// <summary>
    /// Factory helper to build a Server-Side Operation domain model from client wire payload bytes.
    /// </summary>
    /// <param name="reader">A binary reader where the data is read.</param>
    /// <param name="opList">A list of operation types.</param>
    /// <returns>the op is the deserialize operation and the token is a lock token you must dispose later.</returns>
    public static (Operation? op, ReaderWriterLockSlimToken? token) Deserialize(BinaryReader reader, List<OperationType> opList)
    {
      // 1. Read the inner OperationType structural components
      long typeId = reader.ReadInt64();
      if (typeId <= 0 || typeId > opList.Count)
      {
        return (null, null);
      }
      var type = opList.ElementAt((Index)(int)(typeId - 1));
      var opTypeLockToken = type.OpTypeLock.EnterReadLockAsToken();
      if (type.IsDisposed)
        return (null, opTypeLockToken);

      // 2. Read the state context fields matching client serialization layout
      VoteType voteType = (VoteType)reader.ReadByte();
      int stateLength = reader.ReadInt32();
      byte[] stateBytes = reader.ReadBytes(stateLength);

      return (new(type, voteType, stateBytes), opTypeLockToken);
    }
    /// <inheritdoc cref="Deserialize(BinaryReader, List{OperationType})"/>
    public static (Operation? op, ReaderWriterLockSlimToken? token) Deserialize(byte[] bytes, List<OperationType> opList)
    {
      using var ms = new MemoryStream(bytes);
      using var reader = new BinaryReader(ms, Encoding.UTF8);
      return Deserialize(reader, opList);
    }

    /// <summary>
    /// Serializes the server's processed operation back to binary format and write it to a BinaryWriter.
    /// </summary>
    public bool Serialize(BinaryWriter writer)
    {
      // 1. Write the Type parameters
      using(type.OpTypeLock.EnterReadLockAsToken())
      {
        if(type.IsDisposed) return false;
        writer.Write(Type.Id);
      }

      // 2. Write the structural instance metadata (Fixing the client alignment mismatch)
      writer.Write((byte)VoteType);
      writer.Write(StateBytes.Length);
      writer.Write(StateBytes);

      writer.Flush();
      return true;
    }
    /// <summary>
    /// Serializes the server's processed operation back to binary format.
    /// </summary>
    public byte[]? Serialize()
    {
      using var ms = new MemoryStream();
      using var writer = new BinaryWriter(ms, Encoding.UTF8);
      if(!Serialize(writer)) return null;
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