using System.Collections.Concurrent;

namespace operation_vote.Client
{
  public class VotingEnv
  {
    private long idCounter = 0;
    public long NewId => Interlocked.Increment(ref idCounter);
    public void Reset()
    {
      idCounter = 0;
    }
    public readonly ConcurrentDictionary<string, Operation.OperationType> types = [];
  }
}