namespace operation_vote.Interface.Shared
{
  public static class AsyncHelper
  {
    public static AsyncTuple AsyncCall(Action action, bool useThreads, string? name = null)
    {
      if (useThreads)
      {
        Thread resultThread = new(() => action())
        {
          IsBackground = true,
          Name = name
        };
        resultThread.Start();
        return new(null, resultThread);
      }
      else
      {
        Task resultTask = Task.Run(action);
        return new(resultTask, null);
      }
    }
    public static AsyncTuple Prepare(Action action, bool useThreads, string? name = null)
    {
      if (useThreads)
      {
        Thread resultThread = new(() => action())
        {
          IsBackground = true,
          Name = name
        };
        return new(null, resultThread);
      }
      else
      {
        Task resultTask = new(action);
        return new(resultTask, null);
      }
    }
    public record AsyncTuple(Task? Task, Thread? Thread)
    {
      public void Join()
      {
        Task?.GetAwaiter().GetResult();
        Thread?.Join();
      }
      public async Task JoinAsync()
      {
        if (Task != null)
        {
          await Task;
        }

        if (Thread != null && Thread.IsAlive)
        {
          await Task.Run(Thread.Join);
        }
      }
      public void Start()
      {
        Task?.Start();
        Thread?.Start();
      }
    }
  }
}