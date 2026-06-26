using System.Net;
using System.Text;

namespace operation_vote.Interface.Shared
{
  public static class InstructionsProtocol
  {
    public static readonly string Version = "1.2";
    public static Instructions DeserializeInstructions(byte[] instructions)
    {
      var instructionsStrings = new List<string>();
      using var ms = new MemoryStream(instructions);
      using var reader = new BinaryReader(ms, Encoding.UTF8);
      TimeSpan? afk = null;

      try
      {
        var header = reader.ReadString();
        if (header != $"V{Version}") throw new ProtocolViolationException("Instructions protocol is mismatched.");

        while (ms.Position < ms.Length)
        {
          var instructionType = reader.ReadString();
          switch (instructionType)
          {
            case "keys:":
              var keysLength = reader.Read7BitEncodedInt64();
              for (int i = 0; i < keysLength; ++i)
              {
                instructionsStrings.Add(reader.ReadString());
              }
              break;
            case "afk:":
              afk = TimeSpan.FromMilliseconds(reader.ReadDouble());
              Console.WriteLine($"Read AFK time: {afk}");
              break;
          }
        }
      }
      catch (Exception parseEx)
      {
        throw new ProtocolViolationException($"Instructions parse error: {parseEx.Message}");
      }
      return new(Keys: [.. instructionsStrings], AfkLimit: afk);
    }

    public static byte[] SerializeInstructions(string[] keys, TimeSpan? afk)
    {
      using var ms = new MemoryStream();
      using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
      {
        writer.Write($"V{Version}");
        writer.Write("keys:");
        writer.Write7BitEncodedInt64(keys.LongLength);
        foreach (var str in keys)
        {
          writer.Write(str);
        }
        if(afk != null)
        {
          writer.Write("afk:");
          writer.Write((double)afk.Value.TotalMilliseconds);
        }
      }
      return ms.ToArray();
    }
    public static byte[] SerializeInstructions(Instructions instructions) => SerializeInstructions(instructions.Keys, instructions.AfkLimit);
  }
}