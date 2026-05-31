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
        // UPDATED: Now stores both the OperationType metadata and the actual VoteType selection
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<long, (Operation.OperationType Type, VoteType Vote)>> _clientVotes = new();

        // --- EVENT SYSTEM HOOKS ---
        
        /// <summary>
        /// Fires whenever an operation type's metrics (Voters or Supporters) update dynamically.
        /// </summary>
        public event EventHandler<(Operation.OperationType Operation, int Voters, int Supporters)>? OnOperationCountChanged;

        /// <summary>
        /// Fires when a client disconnects, giving an overview of all operation types where their choices were cleared.
        /// </summary>
        public event EventHandler<(Guid ClientId, IReadOnlyCollection<Operation.OperationType> AffectedOperations)>? OnClientVoteCleared;


        /// <summary>
        /// Instantiates the metrics management manager over an API-driven VotingServer instance.
        /// </summary>
        public VotingManager(VotingServer server)
        {
            _server = server;

            // Wire up internal tracking to the server API hooks
            _server.OnOperationReceived += HandleOperationReceived;
            _server.OnClientDisconnected += HandleClientDisconnected;
        }

        /// <summary>
        /// Public API method to fetch the immediate runtime Voters and Supporters metrics for an explicit OperationType.
        /// </summary>
        /// <param name="operationTypeId">The unique tracking ID assigned by the server for that template type.</param>
        /// <returns>A tuple container reflecting active counts of (Voters, Supporters).</returns>
        public (int Voters, int Supporters) GetMetrics(long operationTypeId)
        {
            if (_tallies.TryGetValue(operationTypeId, out var tally))
            {
                return (tally.Voters, tally.Supporters);
            }
            return (0, 0);
        }

        private void HandleOperationReceived(object? sender, (Guid ClientId, Operation ReceivedOperation) e)
        {
            var opType = e.ReceivedOperation.Type;
            long typeId = opType.Id;
            Guid clientId = e.ClientId;
            var voteResult = e.ReceivedOperation.VoteType;

            // 2. Safely capture the operational metric slot
            var tally = _tallies.GetOrAdd(typeId, _ => new OperationTally());

            // 3. Thread-safely mutate client track records using factories
            // UPDATED: Map factory uses the new tuple configuration format
            var operationsMap = _clientVotes.GetOrAdd(clientId, _ => new ConcurrentDictionary<long, (Operation.OperationType, VoteType)>());

            operationsMap.AddOrUpdate(typeId,
                addValueFactory: (key) =>
                {
                    ApplyVoteTally(tally, voteResult);
                    
                    // Publish real-time metric change event passing the full type reference object
                    OnOperationCountChanged?.Invoke(this, (opType, tally.Voters, tally.Supporters));
                    return (opType, voteResult);
                },
                updateValueFactory: (key, previousValue) =>
                {
                    // Clean out old weight tracking values using the tuple's old vote selection
                    RemoveVoteTally(tally, previousValue.Vote);
                    
                    // Append new tracking layout choices
                    ApplyVoteTally(tally, voteResult);

                    // Publish real-time metric change event passing the full type reference object
                    OnOperationCountChanged?.Invoke(this, (opType, tally.Voters, tally.Supporters));
                    return (opType, voteResult);
                }
            );
        }

        private void HandleClientDisconnected(object? sender, (Guid ClientId, string Reason) e)
        {
            Guid clientId = e.ClientId;

            // Extract the connection footprint mapping and strip the user's choices out permanently
            if (_clientVotes.TryRemove(clientId, out var operationsMap))
            {
                var affectedOperations = new List<Operation.OperationType>();

                foreach (var kvp in operationsMap)
                {
                    long typeId = kvp.Key;
                    // UPDATED: Extract values directly from our rich data footprint tuple structure
                    var (opType, historicalVote) = kvp.Value;

                    if (_tallies.TryGetValue(typeId, out var tally))
                    {
                        RemoveVoteTally(tally, historicalVote);
                        affectedOperations.Add(opType);
                        
                        // Broadcast individual modifications directly using our local cache object
                        OnOperationCountChanged?.Invoke(this, (opType, tally.Voters, tally.Supporters));
                    }
                }

                // Fire event summarizing connection cleanup completions 
                if (affectedOperations.Count > 0)
                {
                    OnClientVoteCleared?.Invoke(this, (clientId, affectedOperations.AsReadOnly()));
                }
            }
        }

        private static void ApplyVoteTally(OperationTally tally, VoteType type)
        {
            switch (type)
            {
                case VoteType.Support:
                    Interlocked.Increment(ref tally.VotersField);
                    Interlocked.Increment(ref tally.SupportersField);
                    break;
                case VoteType.Against:
                    Interlocked.Increment(ref tally.VotersField);
                    break;
                case VoteType.Abstain:
                    break;
            }
        }

        private static void RemoveVoteTally(OperationTally tally, VoteType type)
        {
            switch (type)
            {
                case VoteType.Support:
                    Interlocked.Decrement(ref tally.VotersField);
                    Interlocked.Decrement(ref tally.SupportersField);
                    break;
                case VoteType.Against:
                    Interlocked.Decrement(ref tally.VotersField);
                    break;
                case VoteType.Abstain:
                    break;
            }
        }

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