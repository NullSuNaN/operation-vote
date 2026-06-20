using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using operation_vote.Client;
using operation_vote.Client.Request;
using operation_vote.Shared;

namespace operation_vote.Interface.Shared
{
  public static class ClientHelper
  {
    /// <summary>
    /// Access within a AfkLock
    /// </summary>
    public class AfkProcessor : IDisposable
    {
      public readonly SemaphoreSlim AfkSignal = new(0, 1);
      public readonly ReaderWriterLockSlim AfkLock = new();
      public Task? AfkLoopTask {get; private set;} = null;
      public CancellationTokenSource? AfkCts;
      public long LastClickTicks = DateTime.UtcNow.Ticks;
      private SortedList<TimeSpan, Operation.OperationType>? _timeouts = null;
      public ReadOnlyDictionary<TimeSpan, Operation.OperationType>? Timeouts => _timeouts?.AsReadOnly();
      /// <summary>
      /// Also use within a AfkLock
      /// </summary>
      public void SetTimeouts(SortedList<TimeSpan, Operation.OperationType> value)
      {
        _timeouts=value;
      }
      private async Task AFKProcessingThreadAsync(CancellationToken token)
      {
        while (!token.IsCancellationRequested)
        {
          SortedList<TimeSpan, Operation.OperationType>? localTimeouts;
          long clickTicks;

          using(AfkLock.EnterReadLockAsToken())
          {
            localTimeouts = _timeouts;
            clickTicks = Volatile.Read(ref LastClickTicks);
          }

          if (localTimeouts == null)
          {
            try { await AfkSignal.WaitAsync(token); } catch (OperationCanceledException) { break; }
            continue;
          }

          DateTime lastClickTime = new(Volatile.Read(ref clickTicks), DateTimeKind.Utc);
          TimeSpan lowestWait = TimeSpan.MaxValue;
          Operation.OperationType operationType;

          foreach (var item in localTimeouts)
          {
            TimeSpan calculatedDeadline = item.Key;
            TimeSpan elapsed = DateTime.UtcNow - lastClickTime;
            TimeSpan remainingWait = calculatedDeadline - elapsed;

            if (remainingWait <= TimeSpan.Zero)
            {
              operationType = item.Value;
              break;
            }
            else if (remainingWait < lowestWait)
            {
              operationType = item.Value;
              lowestWait = remainingWait;
            }
          }
          {
            int waitMilliseconds = lowestWait == TimeSpan.MaxValue ? -1 : (int)lowestWait.TotalMilliseconds;
            try
            {
              await AfkSignal.WaitAsync(waitMilliseconds, token);
            }
            catch (OperationCanceledException)
            {
              break;
            }
          }
        }
      }
      private AfkProcessor()
      {
        AfkCts = new CancellationTokenSource();
      }
      public static AfkProcessor LaunchAfkProcessor()
      {
        AfkProcessor processor = new();
        processor.AfkLoopTask = processor.AFKProcessingThreadAsync(processor.AfkCts?.Token ?? default);
        return processor;
      }

      public void Dispose()
      {
        GC.SuppressFinalize(this);
        try { AfkCts?.Cancel(); } catch { /* no-op */ }
        try { AfkCts?.Dispose(); } catch { /* no-op */ }
        try { AfkSignal?.Dispose(); } catch { /* no-op */ }
        try { AfkLock?.Dispose(); } catch { /* no-op */ }
      }
    }
    public class OperationManager<T> : IDisposable where T : ISocketRequestHandler
    {
      private readonly ConcurrentDictionary<string, Operation.OperationType> _keyOpMapping = new();
      public ReadOnlyDictionary<string, Operation.OperationType> KeyOpMapping => _keyOpMapping.AsReadOnly();
  		private volatile CancellationTokenSource SendOpCts = new();
      private readonly ReaderWriterLockSlim keyOpReady = new();
      private ReaderWriterLockSlimToken? keyOpReadyToken = null;
      public readonly VotingClient<T> AttachedClient;
      public event EventHandler<(SortedList<TimeSpan, Operation.OperationType> ParsedTimeout, VotingClient<T> AttachedClient)>? OnOperationsReloaded;
      private OperationManager(VotingClient<T> client)
      {
        AttachedClient = client;
      }
      public static OperationManager<T> CreateManager(VotingClient<T> client, out SortedList<TimeSpan, Operation.OperationType> parsedTimeouts)
      {
        OperationManager<T>? manager = new(client);
        client.BeforeOperationsReload += manager.HandleBeforeOperationsReload;
        client.AfterOperationsReload += manager.HandleAfterOperationsReload;
        client.OnConnectionFinished += manager.HandleConnectionFinished;
        using var _ = manager.keyOpReady.EnterWriteLockAsToken();
        manager.ParseInstructions(out parsedTimeouts);
        return manager;
      }

      private void ParseInstructions(out SortedList<TimeSpan, Operation.OperationType> parsedTimeouts)
      {
        _keyOpMapping.Clear();
        var availableOpTypes = AttachedClient.OperationTypes.Values.ToList();
        parsedTimeouts = [];

        foreach (var opType in availableOpTypes)
        {
          if (opType?.Instructions == null || opType.Instructions.Length == 0) continue;

          var (instructionsStrings, afk) = InstructionsProtocol.DeserializeInstructions(opType.Instructions);

          foreach (string key in instructionsStrings)
          {
            if (!string.IsNullOrEmpty(key)) _keyOpMapping[key] = opType;
          }

          if (afk != null)
          {
            parsedTimeouts[afk.Value] = opType;
          }
        }
      }

      private void HandleBeforeOperationsReload(object? sender, EventArgs _)
      {
        SendOpCts.Cancel();
        keyOpReadyToken = keyOpReady.EnterWriteLockAsToken();
      }
      private void HandleAfterOperationsReload(object? sender, bool _)
      {
        ParseInstructions(out var parsedTimeouts);
        SendOpCts=new();
        Interlocked.Exchange(ref keyOpReadyToken, null)?.Dispose();
        OnOperationsReloaded?.Invoke(this, (parsedTimeouts, AttachedClient));
      }
      private void HandleConnectionFinished(object? sender, EventArgs _)
      {
        SendOpCts.Cancel();
        keyOpReadyToken = keyOpReady.EnterWriteLockAsToken();
        ParseInstructions(out var parsedTimeouts);
        SendOpCts=new();
        Interlocked.Exchange(ref keyOpReadyToken, null)?.Dispose();
        OnOperationsReloaded?.Invoke(this, (parsedTimeouts, AttachedClient));
      }

      /// <summary>
      /// Run a function with internal read lock and the operation type of the key.
      /// </summary>
      /// <remarks>
      /// The function cannot call itself. <br/>
      /// If you perform ANY operation related to the client, use the CancellationToken.
      /// </remarks>
      /// <param name="action">
      ///   The action to run with a parameter as the operation type(null if there is no operation attached).<br/>
      ///   If you perform ANY operation related to the client, use the CancellationToken.<br/>
      ///   Protected by a inner lock,cannot call <see cref="RunWithOpType(Action{ValueTuple{Operation.OperationType, CancellationToken}?}, string)"/>.
      /// </param>
      /// <param name="keyStr">the name of the key</param>
      public void RunWithOpType(Action<(Operation.OperationType Type, CancellationToken CancellationToken)?> action, string keyStr)
      {
        using var _ = keyOpReady.EnterReadLockAsToken();
        _keyOpMapping.TryGetValue(keyStr, out var targetedOpType);
        action(targetedOpType == null ? null : (targetedOpType, SendOpCts.Token));
      }
      /// <inheritdoc cref="RunWithOpType(Action{ValueTuple{Operation.OperationType, CancellationToken}?}, string)"/>
      public async Task RunWithOpType(Func<(Operation.OperationType Type, CancellationToken CancellationToken)?, Task> action, string keyStr)
      {
        using var _ = keyOpReady.EnterReadLockAsToken();
        _keyOpMapping.TryGetValue(keyStr, out var targetedOpType);
        await action(targetedOpType == null ? null : (targetedOpType, SendOpCts.Token));
      }

      public void Dispose()
      {
        GC.SuppressFinalize(this);
        AttachedClient.BeforeOperationsReload -= HandleBeforeOperationsReload;
        AttachedClient.AfterOperationsReload -= HandleAfterOperationsReload;
        AttachedClient.OnConnectionFinished -= HandleConnectionFinished;
      }
    }
  }
}