using System.Text;
using operation_vote.Shared;

namespace operation_vote.Client
{
  /// <summary>
  /// A Operation.
  /// There can be multiple operations with the same type.
  /// </summary>
  public class Operation(Operation.OperationType type, VoteType VoteType, byte[] stateBytes) : IDisposable
  {
    public class OperationType
    {
      public readonly VotingEnv Env;
      public readonly long Id;
      /// <summary>
      /// tells the external program what does the operation do.
      /// </summary>
      public readonly byte[] Instructions;

      /// <summary>
      /// You should not create a operation type by your own, this is created by the server.
      /// </summary>
      /// <param name="id">the id sent from the server</param>
      /// <param name="env">the environment</param>
      internal OperationType(byte[] instructions, long id, VotingEnv env)
      {
        Instructions=instructions;
        Id = id;
        Env = env;
      }
    }

    public readonly OperationType Type = type;
    public readonly long Id = type.Env.NewId;
    public byte[] StateBytes { get; set; } = stateBytes;
    public readonly VoteType VoteType = VoteType;
    private bool _isDisposed;
    public bool IsDisposed => _isDisposed;

    /// <summary>
    /// Serializes the entire Operation object into a flexible byte array payload.
    /// </summary>
    public byte[] ToByteArray()
    {
      using var ms = new MemoryStream();
      using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

      writer.Write(Type.Id);

      writer.Write(Id);
      writer.Write((byte)VoteType);
      writer.Write(StateBytes.Length);
      writer.Write(StateBytes);

      writer.Flush();
      return ms.ToArray();
    }

    /// <summary>
    /// Implements IDisposable cleanup rules.
    /// </summary>
    public void Dispose()
    {
      Dispose(true);
      GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
      if (!_isDisposed)
      {
        if (disposing)
        {
          // Clear references or arrays to help out the Garbage Collector
          StateBytes = [];
        }
        _isDisposed = true;
      }
    }
  }
}