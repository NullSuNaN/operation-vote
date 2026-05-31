using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using operation_vote.Server;
using operation_vote.Server.Network;
using operation_vote.Client;
using operation_vote.Client.Request;
using operation_vote.Shared;

namespace operation_vote
{
  public class Program
  {
    private const string HOST = "127.0.0.1";
    private const int TCP_PORT = 9055;
    private const string WS_URI_PREFIX = "http://127.0.0.1:9056/";

    public static void Main() { }
    public static async Task Demo(string[] args)
    {
      string mode = args.ElementAtOrDefault(0)?.ToLowerInvariant() ?? "client";

      if (mode == "server")
      {
        await RunServerAsync();
      }
      else if (mode == "ws")
      {
        await RunClientWSAsync();
      }
      else
      {
        await RunClientAsync();
      }
    }

    private static async Task RunServerAsync()
    {
      Console.WriteLine("=== INITIALIZING MULTI-PROTOCOL HYBRID VOTING ENGINE ===");

      var tcpDriver = new TcpServerChannel(HOST, TCP_PORT);
      var wsDriver = new WSServerChannel(WS_URI_PREFIX);
      var transportCluster = new List<IServerChannel> { tcpDriver, wsDriver };
      Server.Operation.OperationType[] opTypes = [
        new([], 1),
        new([], 2),
      ];

      var server = new VotingServer(transportCluster, opTypes);
      var manager = new VotingManager(server);

      manager.OnOperationCountChanged += (sender, e) =>
      {
        Console.WriteLine($"[Tally Update] OpType #{e.Operation.Id} -> Voters: {e.Voters}, Supporters: {e.Supporters}");
      };

      manager.OnClientVoteCleared += (sender, e) =>
      {
        Console.WriteLine($"[Tally Restructured] Client {e.ClientId} disconnected. Reverted decisions across {e.AffectedOperations.Count} operations.");
      };

      server.OnClientConnected += (sender, clientId) => Console.WriteLine($"[Cluster Joined] Client ID: {clientId}");
      server.OnClientHandshakeCompleted += (sender, clientId) => Console.WriteLine($"[Handshake Verified] Client {clientId} initialized successfully.");
      server.OnClientDisconnected += (sender, e) => Console.WriteLine($"[Cluster Left] Client ID: {e.ClientId}. Reason: {e.Reason}");

      var serverTask = server.StartAsync();

      Console.WriteLine($"Server active. Hosting TCP ({TCP_PORT}) and WebSockets ({WS_URI_PREFIX}) layers...");
      Console.WriteLine("Press [ENTER] to shut down the server gracefully...");
      Console.ReadLine();

      Console.WriteLine("Broadcasting closing frames and wrapping up operations...");
      await server.BroadcastShutdownSignalAsync();
      server.Stop();
      await serverTask;
      Console.WriteLine("Server shutdown completed smoothly.");
    }
    private static async Task RunClientWSAsync()
    {
      Console.WriteLine("=== STARTING WEBSOCKET VOTING CLIENT RUNTIME DOMAIN ===");

      // 1. Establish structural cancellation tokens to monitor terminal context
      using var cts = new CancellationTokenSource();
      Console.CancelKeyPress += (s, e) =>
      {
        e.Cancel = true;
        cts.Cancel();
      };

      // 2. Point to the WebSocket endpoint configured in your server
      // Note: SocketRequestHandlerWS automatically normalizes 'http://' to 'ws://'
      string wsUri = "ws://127.0.0.1:9056/";

      // Instantiate the WS Request Handler and pair it with the client engine
      var wsHandler = new SocketRequestHandlerWS();
      using var client = new VotingClient<SocketRequestHandlerWS>(wsHandler, wsUri, cts.Token);

      try
      {
        Console.WriteLine($"Connecting to network node via WebSocket at {wsUri}...");
        await client.ConnectAsync();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("WebSocket Handshake accomplished successfully! Session linked.\n");
        Console.ResetColor();

        // 3. Extract and display operational templates pushed down by the server over WS
        var availableOpTypes = client.OperationTypes.Values.ToList();
        Console.WriteLine($"Discovered {availableOpTypes.Count} active operational profiles from server.");

        if (availableOpTypes.Count > 0)
        {
          // Target the first template payload ('Instruction_CastBallot_Alpha')
          var primaryType = availableOpTypes[0];
          string details = Encoding.UTF8.GetString(primaryType.Instructions);
          Console.WriteLine($"Targeting Operation Type #{primaryType.Id} ({details})");

          // Build a simple 1-byte ballot payload state matrix
          byte[] mockVoteDataBytes = [0x01];

          // Instantiate a client-side Operation
          var ballotOperation = new Client.Operation(primaryType, Shared.VoteType.Support, mockVoteDataBytes);

          Console.WriteLine($"Transmitting ballot transaction envelope via WS (ID: {ballotOperation.Id})...");
          await client.SendOperationAsync(ballotOperation, cts.Token);
          Console.WriteLine("Transaction packet passed down to the WebSocket transport pipeline successfully.");
        }

        Console.WriteLine("\nPress [ENTER] to disconnect and terminate WebSocket client session...");
        Console.ReadLine();
      }
      catch (Exception ex)
      {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[Fatal Error] WebSocket client workflow broken: {ex.Message}");
        Console.ResetColor();
      }
    }

    private static async Task RunClientAsync()
    {
      Console.WriteLine("=== STARTING VOTING CLIENT RUNTIME DOMAIN ===");
      using var cts = new CancellationTokenSource();

      using var tcpHandler = new SocketRequestHandlerTCP();
      using var client = new VotingClient<SocketRequestHandlerTCP>(tcpHandler, $"tcp://{HOST}:{TCP_PORT}", cts.Token);

      try
      {
        Console.WriteLine($"Connecting to network node at {HOST}:{TCP_PORT}...");

        // FIX: Instead of writing to the TargetURI property and using a hardcoded delay, 
        // execute the ConnectAsync method. This awaits the completion of the background initialization 
        // handshake thread and populates the operation dictionary safely.
        await client.ConnectAsync();
        Console.WriteLine("Handshake completed. Server connection initialized successfully.");

        var availableOpTypes = client.OperationTypes.Values.ToList();
        Console.WriteLine($"Discovered {availableOpTypes.Count} active operational profiles from server.");

        if (availableOpTypes.Count > 0)
        {
          var primaryType = availableOpTypes[0];
          string details = Encoding.UTF8.GetString(primaryType.Instructions);
          Console.WriteLine($"Targeting Operation Type #{primaryType.Id} ({details})");

          byte[] mockVoteDataBytes = [0x01];

          var ballotOperation = new Client.Operation(primaryType, VoteType.Support, mockVoteDataBytes);

          Console.WriteLine($"Transmitting ballot transaction envelope (ID: {ballotOperation.Id})...");
          await client.SendOperationAsync(ballotOperation, cts.Token);
          Console.WriteLine("Transaction packet passed down to the stream transport pipeline successfully.");
        }
        else
        {
          Console.WriteLine("Warning: No operational templates returned by the target host.");
        }

        Console.WriteLine("Press [ENTER] to disconnect and terminate client session...");
        Console.ReadLine();
      }
      catch (Exception ex)
      {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[Fatal Error] Client workflow broken: {ex.Message}");
        Console.ResetColor();
      }
    }
  }
}