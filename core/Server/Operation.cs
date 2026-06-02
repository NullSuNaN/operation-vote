using System;
using System.IO;
using System.Text;
using operation_vote.Shared;

namespace operation_vote.Server
{
  public class Operation
  {
    public class OperationType
    {
      public long Id { get; }
      public byte[] Instructions { get; }

      public OperationType(byte[] instructions, long id)
      {
        Instructions = instructions ?? throw new ArgumentNullException(nameof(instructions));
        Id = id;
      }
    }

    public OperationType Type { get; }
    public VoteType VoteType { get; }
    public byte[] StateBytes { get; set; }

    public Operation(OperationType type, VoteType voteType, byte[] stateBytes)
    {
      Type = type ?? throw new ArgumentNullException(nameof(type));
      VoteType = voteType;
      StateBytes = stateBytes ?? throw new ArgumentNullException(nameof(stateBytes));
    }

    /// <summary>
    /// Factory helper to build a Server-Side Operation domain model from client wire payload bytes.
    /// </summary>
    public static Operation? Deserialize(byte[] bytes, List<OperationType> opList)
    {
      using var ms = new MemoryStream(bytes);
      using var reader = new BinaryReader(ms, Encoding.UTF8);

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

    /// <summary>
    /// Serializes the server's processed operation back to binary format for client confirmation loops.
    /// </summary>
    public byte[] ToByteArray()
    {
      using var ms = new MemoryStream();
      using var writer = new BinaryWriter(ms, Encoding.UTF8);

      // 1. Write the Type parameters
      writer.Write(Type.Id);

      // 2. Write the structural instance metadata (Fixing the client alignment mismatch)
      writer.Write((byte)VoteType);
      writer.Write(StateBytes.Length);
      writer.Write(StateBytes);

      writer.Flush();
      return ms.ToArray();
    }
  }
}