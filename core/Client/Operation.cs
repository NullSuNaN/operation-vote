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

    /// <summary>
    /// Factory helper to build a Server-Side Operation domain model from client wire payload bytes.
    /// </summary>
    public static Operation? Deserialize(BinaryReader reader, List<OperationType> opList)
    {
      // 1. Read the inner OperationType structural components
      long typeId = reader.ReadInt64();
      if(typeId <= 0 || typeId > opList.Count)
      {
        return null;
      }
      var type = opList.ElementAt((Index)(int)(typeId-1));

      // 2. Read the state context fields matching client serialization layout
      VoteType voteType = (VoteType)reader.ReadByte();
      int stateLength = reader.ReadInt32();
      byte[] stateBytes = reader.ReadBytes(stateLength);

      return new Operation(type, voteType, stateBytes);
    }
    public static Operation? Deserialize(byte[] bytes, List<OperationType> opList)
    {
      using var ms = new MemoryStream(bytes);
      using var reader = new BinaryReader(ms, Encoding.UTF8);
      return Deserialize(reader, opList);
    }

    /// <summary>
    /// Serializes the server's processed operation back to binary format and write it to a BinaryWriter.
    /// </summary>
    public void Serialize(BinaryWriter writer)
    {
      // 1. Write the Type parameters
      writer.Write(Type.Id);

      // 2. Write the structural instance metadata (Fixing the client alignment mismatch)
      writer.Write((byte)VoteType);
      writer.Write(StateBytes.Length);
      writer.Write(StateBytes);

      writer.Flush();
    }
    /// <summary>
    /// Serializes the server's processed operation back to binary format.
    /// </summary>
    public byte[] Serialize()
    {
      using var ms = new MemoryStream();
      using var writer = new BinaryWriter(ms, Encoding.UTF8);
      Serialize(writer);
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