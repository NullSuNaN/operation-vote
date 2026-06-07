using System.Collections.Concurrent;
using operation_vote.Shared;

namespace operation_vote.Server
{
  public class VotingManager
  {
    private readonly VotingServer _server;

    // Tracks state per OperationType ID: [OperationTypeId] -> [TallyCounters]
    private readonly ConcurrentDictionary<long, OperationTally> _tallies = new();

    // Tracks current client state footprints across operation contexts:
    private readonly ConcurrentDictionary<ClientInfo, ConcurrentDictionary<long, (Operation.OperationType Type, VoteType Vote)>> _clientVotes = new();

    // --- EVENT SYSTEM HOOKS ---

    /// <summary>
    /// Fires whenever an operation type's metrics (Voters or Supporters) update dynamically.
    /// </summary>
    public event EventHandler<(Operation.OperationType Operation, int Voters, int Supporters)>? OnOperationCountChanged;

    /// <summary>
    /// Fires when a client disconnects, giving an overview of all operation types where their choices were cleared.
    /// </summary>
    public event EventHandler<(ClientInfo Client, IReadOnlyCollection<Operation.OperationType> AffectedOperations)>? OnClientVoteCleared;

    private readonly ReaderWriterLockSlim managerLock = new();

    /// <summary>
    /// Instantiates the metrics management manager over an API-driven VotingServer instance.
    /// </summary>
    public VotingManager(VotingServer server)
    {
      _server = server;

      // Wire up internal tracking to the server API hooks
      _server.OnOperationReceived += HandleOperationReceived;
      _server.OnClientDisconnected += HandleClientDisconnected;
      _server.UsersOperate(dict =>
      {
        dict.OnUserRegistered += (sender, user) =>
        {
          user.OnVoteMultiplierChange += HandleUserVoteMultiplierChange;
        };
        dict.OnUserDeleted += (sender, user) =>
        {
          managerLock.EnterUpgradeableReadLock();
          try
          {
            int multiplierChange = user.VoteMultiplier - _server.unauthorizedUser.VoteMultiplier;
            foreach (var client in user.ConnectedClients.Keys)
            {
              client.User = _server.unauthorizedUser;
              if (_clientVotes.TryGetValue(client, out var votes))
              {
                foreach (var item in votes)
                  if (_tallies.TryGetValue(item.Value.Type.Id, out var tally))
                  {
                    managerLock.EnterWriteLock();
                    try
                    {
                      RemoveVoteTally(tally, item.Value.Vote, multiplierChange);
                    }
                    finally { managerLock.ExitWriteLock(); }
                  }
              }
            }
          }
          finally { managerLock.ExitUpgradeableReadLock(); }
        };
        foreach (var user in dict.Values)
        {
          user.OnVoteMultiplierChange += HandleUserVoteMultiplierChange;
        }
      });
    }

    /// <summary>
    /// Public API method to fetch the immediate runtime Voters and Supporters metrics for an explicit OperationType.
    /// </summary>
    /// <param name="operationTypeId">The unique tracking ID assigned by the server for that template type.</param>
    /// <returns>A tuple container reflecting active counts of (Voters, Supporters).</returns>
    public (int Voters, int Supporters) GetMetrics(long operationTypeId)
    {
      managerLock.EnterReadLock();
      try
      {
        if (_tallies.TryGetValue(operationTypeId, out var tally))
        {
          return (tally.Voters, tally.Supporters);
        }
        return (0, 0);
      }
      finally { managerLock.ExitReadLock(); }
    }

    private void HandleOperationReceived(object? sender, (ClientInfo Client, Operation ReceivedOperation) e)
    {
      managerLock.EnterWriteLock();
      try
      {
        var opType = e.ReceivedOperation.Type;
        long typeId = opType.Id;
        var voteResult = e.ReceivedOperation.VoteType;

        // 2. Safely capture the operational metric slot
        var tally = _tallies.GetOrAdd(typeId, _ => new OperationTally());

        // 3. Thread-safely mutate client track records using factories
        // UPDATED: Map factory uses the new tuple configuration format
        var operationsMap = _clientVotes.GetOrAdd(e.Client, _ => new ConcurrentDictionary<long, (Operation.OperationType, VoteType)>());

        operationsMap.AddOrUpdate(typeId,
            addValueFactory: (key) =>
            {
              ApplyVoteTally(tally, voteResult, e.Client.User);

              // Publish real-time metric change event passing the full type reference object
              OnOperationCountChanged?.Invoke(this, (opType, tally.Voters, tally.Supporters));
              return (opType, voteResult);
            },
            updateValueFactory: (key, previousValue) =>
            {
              // Clean out old weight tracking values using the tuple's old vote selection
              RemoveVoteTally(tally, previousValue.Vote, e.Client.User);

              // Append new tracking layout choices
              ApplyVoteTally(tally, voteResult, e.Client.User);

              // Publish real-time metric change event passing the full type reference object
              OnOperationCountChanged?.Invoke(this, (opType, tally.Voters, tally.Supporters));
              return (opType, voteResult);
            }
        );
      }
      finally { managerLock.ExitWriteLock(); }
    }

    private void HandleClientDisconnected(object? sender, (ClientInfo Client, string Reason) e)
    {
      managerLock.EnterWriteLock();
      try
      {
        // Extract the connection footprint mapping and strip the user's choices out permanently
        if (_clientVotes.TryRemove(e.Client, out var operationsMap))
        {
          var affectedOperations = new List<Operation.OperationType>();

          foreach (var kvp in operationsMap)
          {
            long typeId = kvp.Key;
            // UPDATED: Extract values directly from our rich data footprint tuple structure
            var (opType, historicalVote) = kvp.Value;

            if (_tallies.TryGetValue(typeId, out var tally))
            {
              RemoveVoteTally(tally, historicalVote, e.Client.User);
              affectedOperations.Add(opType);

              // Broadcast individual modifications directly using our local cache object
              OnOperationCountChanged?.Invoke(this, (opType, tally.Voters, tally.Supporters));
            }
          }

          // Fire event summarizing connection cleanup completions 
          if (affectedOperations.Count > 0)
          {
            OnClientVoteCleared?.Invoke(this, (e.Client, affectedOperations.AsReadOnly()));
          }
        }
      }
      finally { managerLock.ExitWriteLock(); }
    }
    private void HandleUserVoteMultiplierChange(object? sender, (int Original, int New) e)
    {
      managerLock.EnterUpgradeableReadLock();
      User user = (User?)sender ?? null!;
      try
      {
        var clients = user.ConnectedClients.Keys; // get a local copy
        foreach (var client in clients)
        {
          if (_clientVotes.TryGetValue(client, out var votes))
            foreach (var item in votes)
              if (_tallies.TryGetValue(item.Value.Type.Id, out var tally))
              {
                managerLock.EnterWriteLock();
                try
                {
                  ApplyVoteTally(tally, item.Value.Vote, e.New - e.Original);
                }
                finally { managerLock.ExitWriteLock(); }
              }
        }
      }
      finally { managerLock.ExitUpgradeableReadLock(); }
    }

    private static void ApplyVoteTally(OperationTally tally, VoteType type, int multiplier)
    {
      switch (type)
      {
        case VoteType.Support:
          Interlocked.Add(ref tally.VotersField, multiplier);
          Interlocked.Add(ref tally.SupportersField, multiplier);
          break;
        case VoteType.Against:
          Interlocked.Add(ref tally.VotersField, multiplier);
          break;
        case VoteType.Abstain:
          break;
      }
    }
    private static void ApplyVoteTally(OperationTally tally, VoteType type, User user)
      => ApplyVoteTally(tally, type, user.VoteMultiplier);

    private static void RemoveVoteTally(OperationTally tally, VoteType type, int multiplier)
    {
      switch (type)
      {
        case VoteType.Support:
          Interlocked.Add(ref tally.VotersField, -multiplier);
          Interlocked.Add(ref tally.SupportersField, -multiplier);
          break;
        case VoteType.Against:
          Interlocked.Add(ref tally.VotersField, -multiplier);
          break;
        case VoteType.Abstain:
          break;
      }
    }
    private static void RemoveVoteTally(OperationTally tally, VoteType type, User user)
      => RemoveVoteTally(tally, type, user.VoteMultiplier);

    /// <summary>
    /// Internal atomic tally fields structure to guarantee synchronization consistency.
    /// </summary>
    private class OperationTally
    {
      public int VotersField;
      public int SupportersField;

      public int Voters => Volatile.Read(ref VotersField);
      public int Supporters => Volatile.Read(ref SupportersField);
    }
  }
}