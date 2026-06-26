using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using operation_vote.Client;
using operation_vote.Client.Request;
using operation_vote.Shared;
using operation_vote.Shared.Extensions;

namespace operation_vote.Interface.Shared
{
  public static class ClientHelper
  {
    public class AfkProcessor<T> : IDisposable where T : ISocketRequestHandler
    {
      private readonly SemaphoreSlim AfkSignal = new(0, 1);
      private Thread? AfkLoopThread = null;
      private readonly CancellationTokenSource? AfkCts;
      private volatile DateTimeWrapper LastClickTime = new(DateTime.UtcNow);
      private record class DateTimeWrapper(DateTime Value);
      private volatile SortedSet<OperationAfkRecord>? _timeouts = null;
      private readonly VotingClient<T> AttachedClient;
      private volatile Thread? PendingSendThread;
      public ReadOnlySet<OperationAfkRecord>? Timeouts => _timeouts?.AsReadOnly();
      private volatile ConcurrentDictionary<Operation.OperationType, (VoteType Vote, bool IsAfk)> AfkTypes = [];


      private readonly SemaphoreSlim IsNotAfkLock = new(1, 1);
      private volatile CancellationTokenSource SendOperationCts = new();
      private volatile bool IsNotAfkState = false;

      /// <summary>
      /// Update the timeouts.
      /// </summary>
      public void SetTimeouts(SortedSet<OperationAfkRecord>? value)
      {
        _timeouts = value;
        UpdateAfk(null);
      }
      /// <inheritdoc cref="SetTimeouts(SortedSet{OperationAfkRecord}?)"/>
      public async Task SetTimeoutsAsync(SortedSet<OperationAfkRecord>? value) => SetTimeouts(value);
      private void AFKProcessingThreadAsync(CancellationToken token)
      {
        while (!token.IsCancellationRequested)
        {
          SortedSet<OperationAfkRecord>? localTimeouts;
          DateTime lastClickTime;

          localTimeouts = _timeouts;
          lastClickTime = LastClickTime.Value;

          if (!(localTimeouts?.Count > 0))
          {
            try
            {
              IsNotAfkLock.TryRelease();
              AfkSignal.Wait(token);
            }
            catch (OperationCanceledException) { break; }
            continue;
          }


          bool allAfk = true, allNonPositiveRemaining = true;
          foreach (var item in localTimeouts)
          {
            TimeSpan calculatedDeadline = item.Timeout;
            TimeSpan elapsed = DateTime.UtcNow - lastClickTime;
            TimeSpan remainingWait = calculatedDeadline - elapsed;
            try
            {
              bool isTimeout = true;
              if (remainingWait > TimeSpan.Zero)
              {
                isTimeout = !AfkSignal.Wait(remainingWait, token);
                allNonPositiveRemaining = false;
                if (!isTimeout)
                {
                  allAfk = false;
                  break;
                }
              }
              if (isTimeout)
              {
                SendOperationCts.Cancel();
                if (Interlocked.Exchange(ref IsNotAfkState, false) == true)
                  IsNotAfkLock.Wait(token);
                bool sendOp = true;
                AfkTypes.AddOrUpdate(item.Operation, (VoteType.Against, true), (_, originalData) =>
                {
                  sendOp = !originalData.IsAfk;
                  return (originalData.Vote, true);
                });
                if (sendOp)
                {
                  Thread localPendingSendThread = new(() =>
                  {
                    if (!AttachedClient.SendOperationAsync(new(item.Operation, VoteType.Abstain, []), token).GetAwaiter().GetResult())
                    {
                      AttachedClient.Dispose();
                      Dispose();
                      return;
                    }
                  })
                  {
                    IsBackground = false,
                    Name = $"Send Operation {item.Operation.Id} Afk Thread"
                  };
                  Interlocked.Exchange(ref PendingSendThread, localPendingSendThread)?.Join();
                  localPendingSendThread.Start();
                }
              }
            }
            catch (OperationCanceledException)
            {
              allAfk = false;
              SendOperationCts.Cancel();
              break;
            }
          }
          try
          {
            if (!allAfk)
            {
              if (Interlocked.Exchange(ref IsNotAfkState, true) == false)
                IsNotAfkLock.TryRelease();
              AfkSignal.Wait(token);
              TryRestore(token);
            }
            else if (allNonPositiveRemaining)
            {
              IsNotAfkLock.TryRelease();
              AfkSignal.Wait(token);
            }
          }
          catch (OperationCanceledException) { }
        }
        SendOperationCts.Cancel();
      }
      private void TryRestore(CancellationToken token)
      {
        var localAfkTypes = AfkTypes.ToArray();
        if (Interlocked.Exchange(ref IsNotAfkState, true) == false)
        {
          void resetAction()
          {
            foreach (var (index, item) in localAfkTypes.Index())
            {
              if (item.Value.IsAfk)
              {
                if (!AttachedClient.SendOperationAsync(new(item.Key, item.Value.Vote, []), token).GetAwaiter().GetResult())
                {
                  AttachedClient.Dispose();
                  Dispose();
                  return;
                }
                localAfkTypes[index] = new(item.Key, (item.Value.Vote, false));
              }
              if (token.IsCancellationRequested) break;
            }
            AfkTypes = new(localAfkTypes);
          }
          Thread localPendingSendThread = new(resetAction)
          {
            IsBackground = false,
            Name = "Reset Afk Operations Thread"
          };
          Interlocked.Exchange(ref PendingSendThread, localPendingSendThread)?.Join();
          localPendingSendThread.Start();
        }
      }
      private AfkProcessor(VotingClient<T> attachedClient)
      {
        AfkCts = new();
        AttachedClient = attachedClient;
      }
      public static AfkProcessor<T> LaunchAfkProcessor(VotingClient<T> attachedClient)
      {
        AfkProcessor<T> processor = new(attachedClient)
        {
          LastClickTime = new(DateTime.UtcNow)
        };
        processor.AfkLoopThread = new Thread(() => processor.AFKProcessingThreadAsync(processor.AfkCts?.Token ?? default))
        {
          IsBackground = false,
          Name = "Afk Processor Thread"
        };
        processor.AfkLoopThread.Start();
        return processor;
      }
      /// <summary>
      /// Signal the <see cref="AfkProcessor{T}"/> that the user operated.
      /// </summary>
      /// <param name="sendRequest">a non-async function that would block the <see cref="AfkProcessor{T}"/> request, send the requests here.</param>
      /// <param name="operation">The operation</param>
      /// <param name="vote">The vote</param>
      public void UpdateAfk(Action<CancellationToken>? sendRequest, Operation.OperationType operation, VoteType vote)
      {
        SendOperationCts = new();
        AfkSignal.TryRelease();
        using var _ = IsNotAfkLock.WaitAsToken().SetTrying(true);
        if (sendRequest is not null)
          sendRequest(SendOperationCts.Token);
        LastClickTime = new(DateTime.UtcNow);
        AfkTypes.AddOrUpdate(operation, (vote, false), (_, __) => (vote, false));
      }
      /// <inheritdoc cref="UpdateAfk(Action{CancellationToken}?, Operation.OperationType, VoteType)"/>
      public async Task UpdateAfkAsync(Func<CancellationToken, Task>? sendRequest, Operation.OperationType operation, VoteType vote) =>
        UpdateAfk(sendRequest is null ? null : token=>sendRequest(token).GetAwaiter().GetResult(), operation, vote);
      /// <inheritdoc cref="UpdateAfk(Action{CancellationToken}?, Operation.OperationType, VoteType)"/>
      /// <remarks>
      /// use <see cref="UpdateAfk(Action{CancellationToken}?, Operation.OperationType, VoteType)"/> if the signal would trigger an operation.
      /// </remarks>
      public void UpdateAfk(Action<CancellationToken>? sendRequest)
      {
        SendOperationCts = new();
        AfkSignal.TryRelease();
        using var _ = IsNotAfkLock.WaitAsToken().SetTrying(true);
        if (sendRequest is not null)
          sendRequest(SendOperationCts.Token);
        LastClickTime = new(DateTime.UtcNow);
      }
      /// <inheritdoc cref="UpdateAfk(Action{CancellationToken}?, Operation.OperationType, VoteType)"/>
      /// <remarks>
      /// use <see cref="UpdateAfkAsync(Action{CancellationToken}?, Operation.OperationType, VoteType)"/> if the signal would trigger an operation.
      /// </remarks>
      public async Task UpdateAfkAsync(Func<CancellationToken, Task>? sendRequest)
      {
        SendOperationCts = new();
        AfkSignal.TryRelease();
        using var _ = IsNotAfkLock.WaitAsToken().SetTrying(true);
        if (sendRequest is not null)
          await sendRequest(SendOperationCts.Token);
        LastClickTime = new(DateTime.UtcNow);
      }

      public void Dispose()
      {
        GC.SuppressFinalize(this);
        try { AfkCts?.Cancel(); } catch { /* no-op */ }
        try { SendOperationCts?.Cancel(); } catch { /* no-op */ }
        AfkLoopThread?.Join();
      }
    }
    public class OperationManager<T> : IDisposable where T : ISocketRequestHandler
    {
      private readonly ConcurrentDictionary<string, Operation.OperationType> _keyOpMapping = new();
      public ReadOnlyDictionary<string, Operation.OperationType> KeyOpMapping => _keyOpMapping.AsReadOnly();
      private volatile CancellationTokenSource SendOpCts = new();
      private readonly CrossThreadReaderWriterLock keyOpReady = new();
      private CrossThreadReaderWriterLock.CrossThreadLockToken? keyOpReadyToken;
      public readonly VotingClient<T> AttachedClient;
      public event EventHandler<(SortedSet<OperationAfkRecord> ParsedTimeout, VotingClient<T> AttachedClient)>? OnOperationsReloaded;
      private OperationManager(VotingClient<T> client)
      {
        AttachedClient = client;
      }
      public static OperationManager<T> CreateManager(VotingClient<T> client, out SortedSet<OperationAfkRecord> parsedTimeouts)
      {
        OperationManager<T>? manager = new(client);
        client.BeforeOperationsReload += manager.HandleBeforeOperationsReload;
        client.AfterOperationsReload += manager.HandleAfterOperationsReload;
        client.OnConnectionFinished += manager.HandleConnectionFinished;
        using var _ = manager.keyOpReady.EnterWriteLockAsToken();
        manager.ParseInstructions(out parsedTimeouts);
        return manager;
      }

      private void ParseInstructions(out SortedSet<OperationAfkRecord> parsedTimeouts)
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
            parsedTimeouts.Add(new(afk.Value, opType));
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
        SendOpCts = new();
        Interlocked.Exchange(ref keyOpReadyToken, null)?.Dispose();
        OnOperationsReloaded?.Invoke(this, (parsedTimeouts, AttachedClient));
      }
      private void HandleConnectionFinished(object? sender, EventArgs _)
      {
        SendOpCts.Cancel();
        var readyToken = keyOpReady.EnterWriteLockAsToken();
        keyOpReadyToken = readyToken;
        ParseInstructions(out var parsedTimeouts);
        SendOpCts = new();
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
    public record OperationAfkRecord(TimeSpan Timeout, Operation.OperationType Operation) : IComparable<OperationAfkRecord>
    {
      public int CompareTo(OperationAfkRecord? other)
      {
        int timeoutResult = Timeout.CompareTo(other?.Timeout);
        return timeoutResult != 0 ? timeoutResult : Operation.CompareTo(other?.Operation);
      }
    }
  }
}