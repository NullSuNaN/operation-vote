using System.Diagnostics;

namespace operation_vote.Shared.Extensions
{
  public static class SemaphoreSlimExtensions
  {

    public static SemaphoreSlimToken WaitAsToken(this SemaphoreSlim @lock)
    {
      @lock.Wait();
      return new(@lock);
    }
    public static async Task<SemaphoreSlimToken> WaitAsyncAsToken(this SemaphoreSlim @lock)
    {
      await @lock.WaitAsync();
      return new(@lock);
    }
    public static bool TryRelease(this SemaphoreSlim @lock)
    {
      try
      {
         @lock.Release();
        return true;
      }
      catch (SemaphoreFullException)
      {
        return false;
      }
    }
  }
  public class SemaphoreSlimToken : IDisposable
  {
    public void Dispose()
    {
      GC.SuppressFinalize(this);
      if(TryReleasing)
        targetLock.TryRelease();
      else
        targetLock.Release();
    }

    private readonly SemaphoreSlim targetLock;
    private volatile bool TryReleasing = false;
    internal SemaphoreSlimToken(SemaphoreSlim targetLock)
    {
      this.targetLock = targetLock;
    }
    public SemaphoreSlimToken SetTrying(bool tryReleasing)
    {
      TryReleasing = tryReleasing;
      return this;
    }

    ~SemaphoreSlimToken()
    {
      Console.ForegroundColor = ConsoleColor.Red;
      Console.Error.WriteLine($"A {typeof(SemaphoreSlimToken)} is not disposed!\a");
      Console.ForegroundColor = ConsoleColor.White;
      Debugger.Break();
    }
  }
}