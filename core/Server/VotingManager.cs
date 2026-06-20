using System.Collections.Concurrent;
using operation_vote.Shared;

namespace operation_vote.Server
{
  /// <summary>
  /// A default processor of the server data, responsible for counting the votes.<br/>
  /// A <see cref="VotingServer"/> managed by <see cref="VotingManager"/> should not be accessed.
  /// </summary>
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
    /// Instantiates the metrics management manager over an API-driven VotingServer instance.<br/>
    /// The <see cref="VotingServer"/> should not be launched when initializing and launched later manually.
    /// </summary>
    public VotingManager(VotingServer server)
    {
      _server = server;

      // Wire up internal tracking to the server API hooks
      _server.OnOperationReceived += HandleOperationReceived;
      _server.OnClientDisconnected += HandleClientDisconnected;
      _server.OnUserRegistered += (sender, user) =>
      {
        Console.WriteLine($"OnUserRegistered: {user.Name}");
        user.OnVoteMultiplierChange += HandleUserVoteMultiplierChange;
      };
      _server.OnClientAuthorized += (sender, e) =>
      {
        Console.WriteLine($"OnClientAuthorized: {e.User.Name}");
        using var _ = managerLock.EnterUpgradeableReadLockAsToken();
        var (client, user) = e;
        try
        {
          int multiplierChange = user.VoteMultiplier - _server.UnauthorizedUser.VoteMultiplier;
          if(multiplierChange != 0)
            using(managerLock.EnterWriteLockAsToken())
              ProcessVoteMultiplierChange(client, multiplierChange);
        }
        finally { }
      };
      _server.OnClientUnauthorized += (sender, e) =>
      {
        Console.WriteLine($"OnClientUnauthorized: {e.User.Name}");
        using var _ = managerLock.EnterUpgradeableReadLockAsToken();
        var (client, user) = e;
        try
        {
          int multiplierChange = _server.UnauthorizedUser.VoteMultiplier - user.VoteMultiplier;
          if(multiplierChange != 0)
            using(managerLock.EnterWriteLockAsToken())
              ProcessVoteMultiplierChange(client, multiplierChange);
        }
        finally { }
      };

      _server.OnUserDeleted += (sender, e) =>
      {
        Console.WriteLine($"OnUserDeleted: {e.User.Name}");
        managerLock.EnterUpgradeableReadLock();
        e.User.OnVoteMultiplierChange -= HandleUserVoteMultiplierChange;
      };
      foreach (var item in server.Users.Values)
      {
        item.OnVoteMultiplierChange += HandleUserVoteMultiplierChange;
      }
      server.UnauthorizedUser.OnVoteMultiplierChange += HandleUserVoteMultiplierChange; // Anonymous is a separate field
    }
    private void ProcessVoteMultiplierChange(ClientInfo client, int multiplierChange)
    {
      if (_clientVotes.TryGetValue(client, out var votes))
        foreach (var item in votes)
          if (_tallies.TryGetValue(item.Value.Type.Id, out var tally))
          {
            managerLock.EnterWriteLock();
            try
            {
              RemoveVoteTally(tally, item.Value.Vote, multiplierChange);
              OnOperationCountChanged?.Invoke(this, (item.Value.Type, tally.Voters, tally.Supporters));
            }
            finally { managerLock.ExitWriteLock(); }
          }
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

    /// <summary>
    /// Signal a unhandled user change to the manager. <br/>
    /// </summary>
    /// <remarks>
    /// These includes:
    /// <list>
    ///   <item> A <c>User</c> property change of a <see cref="ClientInfo"/> from a external source instead of the server. </item>
    ///   <item> A <see cref="IUserContainer"/> change that does not trigger either <see cref="IUserContainer.OnUserRegistered"/> or <see cref="IUserContainer.OnUserDeleted"/>. </item>
    /// </list>
    /// Does not include:
    /// <list>
    ///   <item> A user multiplier change of a client from a external source instead of the server. </item>
    /// </list>
    /// </remarks>
    /// <param name="client">the client</param>
    /// <param name="oldUser">the original user the client was linked to</param>
    public void SignalUnhandledUserChange(ClientInfo client, User oldUser)
    {
      using var _ = managerLock.EnterUpgradeableReadLockAsToken();
      if(client.User.VoteMultiplier == oldUser.VoteMultiplier) return;
      int multiplierChange = client.User.VoteMultiplier - oldUser.VoteMultiplier;
      try
      {
        ProcessVoteMultiplierChange(client, multiplierChange);
      }
      finally { }
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
      User user = (User?)sender ?? null!;
      Console.WriteLine($"OnUserVoteMultiplierChange: {user.Name}: {e.Original} -> {e.New}");
      // Step 1: Snapshot the client list under a READ lock only, with NO managerLock held.
      // This avoids the lock-order inversion deadlock with the disconnect handler
      // which acquires ConnectedClientsLock then managerLock.
      List<ClientInfo> clients;
      using (user.ConnectedClientsLock.EnterReadLockAsToken())
        clients = user.ConnectedClients.Keys.ToList();

      // Step 2: Adjust tallies under a single managerLock write lock.
      managerLock.EnterWriteLock();
      try
      {
        foreach (var client in clients)
        {
          if (_clientVotes.TryGetValue(client, out var votes))
            foreach (var item in votes)
              if (_tallies.TryGetValue(item.Value.Type.Id, out var tally))
              {
                ApplyVoteTally(tally, item.Value.Vote, e.New - e.Original);
                OnOperationCountChanged?.Invoke(this, (item.Value.Type, tally.Voters, tally.Supporters));
              }
        }
      }
      finally { managerLock.ExitWriteLock(); }
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