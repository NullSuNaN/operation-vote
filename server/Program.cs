using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using operation_vote.Interface.Shared;
using operation_vote.Server;
using operation_vote.Server.Network;

namespace operation_vote.Interface.Server
{
  // Updated configurations structures matching the polymorphic results definitions array
  public record NetworkConfig(
    [property: JsonPropertyName("TcpHost")] string TcpHost,
    [property: JsonPropertyName("TcpPort")] int TcpPort,
    [property: JsonPropertyName("WsUriPrefix")] string WsUriPrefix
  );

  public record ProfileConfig(
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("Keys")] string[] Keys,
    [property: JsonPropertyName("VoteResults")] List<BaseResultConfig> VoteResults,
    [property: JsonPropertyName("AfkLimit")] string? AfkLimit
  );

  public record ServerAppConfig(
    [property: JsonPropertyName("Network")] NetworkConfig Network,
    [property: JsonPropertyName("Profiles")] List<ProfileConfig> Profiles
  );

  internal class Program
  {
    private static long _idCounter = 0;
    public static long NewId => Interlocked.Increment(ref _idCounter);
    public static UserDatabase userDB = null!;
    public static async Task<int> Main(string[] args)
    {
      userDB=[];
      string? first = args.FirstOrDefault((string?)null);
      string configFile = first ?? "config.json";
      if(first?.StartsWith("--") ?? false)
      {
        if(first.Length == 2)
          configFile = args.ElementAtOrDefault(1) ?? "config.json";
        else if (first == "--manager")
        {
          UserDatabase.RunConsoleManager();
          return 0;
        }
        else
        {
          return 1;
        }
      }
      string configPath = Path.Combine(AppContext.BaseDirectory, first ?? "config.json");

      if (!File.Exists(configPath))
      {
        Console.ForegroundColor = ConsoleColor.Red;
        ServerLogger.logger.LogInformation(()=>$"[Fatal Error] Configuration layout target missing at: {configPath}");
        Console.ResetColor();
        return 0;
      }

      try
      {
        ServerLogger.logger.LogInformation(()=>"=== INITIALIZING ===");
        ServerLogger.logger.LogInformation(()=>$"Loading configuration rules from: {configPath}");

        string jsonText = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize<ServerAppConfig>(jsonText);

        if (config == null || config.Network == null || config.Profiles == null)
        {
          ServerLogger.logger.LogInformation(()=>"[Error] Serialization failed: Root metadata target fields are null.");
          return 0;
        }

        await RunServerWithConfigAsync(config);
      }
      catch (Exception ex)
      {
        Console.ForegroundColor = ConsoleColor.Red;
        ServerLogger.logger.LogInformation(()=>$"[Fatal System Crash] Runtime config parse failure: {ex.Message}");
        if (ex.InnerException != null)
        {
          ServerLogger.logger.LogInformation(()=>$"Details: {ex.InnerException.Message}");
        }
        Console.ResetColor();
      }
      return 0;
    }

    private static async Task RunServerWithConfigAsync(ServerAppConfig config)
    {
      // 1. TCP Driver Binds Directly
      var tcpDriver = string.IsNullOrEmpty(config.Network.TcpHost)
          ? null
          : new TcpServerChannel(config.Network.TcpHost, config.Network.TcpPort);

      // 2. Sanitize and Validate the WebSocket Prefix URI Layout
      var sanitizedWsPrefix = config.Network.WsUriPrefix;
      if (!string.IsNullOrEmpty(sanitizedWsPrefix))
      {
        // Standardize prefixes to prevent protocol activation parsing bugs
        if (sanitizedWsPrefix.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
        {
          sanitizedWsPrefix = sanitizedWsPrefix.Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase);
        }
        else if (sanitizedWsPrefix.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
          sanitizedWsPrefix = sanitizedWsPrefix.Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase);
        }
        else if (!sanitizedWsPrefix.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !sanitizedWsPrefix.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
          sanitizedWsPrefix = "ws://" + sanitizedWsPrefix;
        }

        // Ensure trailing slash is present for the underlying engine matching rules
        if (!sanitizedWsPrefix.EndsWith('/'))
        {
          sanitizedWsPrefix += "/";
        }
      }

      var wsDriver = string.IsNullOrEmpty(sanitizedWsPrefix)
          ? null
          : new WSServerChannel(sanitizedWsPrefix);

      var transportCluster = new List<IConcurrentServerChannel>();
      if (tcpDriver != null) transportCluster.Add(new ConcurrentChannelWrapper<TcpServerChannel>(tcpDriver));
      if (wsDriver != null) transportCluster.Add(new ConcurrentChannelWrapper<WSServerChannel>(wsDriver));

      var operationalSuite = new List<Operation.OperationType>();

      // Store profiles using internal tracking IDs to link execution updates accurately
      var profileMapping = new Dictionary<long, ProfileConfig>();

      foreach (var profile in config.Profiles)
      {
        long targetId = NewId;
        TimeSpan? afkSpan = null;
        if(profile.AfkLimit != null)
        {
          if(TimeSpan.TryParse(profile.AfkLimit.AsSpan(), out var _afkSpan)){
            afkSpan=_afkSpan;
            ServerLogger.logger.LogInformation(()=>$"Parsed AFK time limit for {profile.Name}: {profile.AfkLimit} as {_afkSpan}");
          }
          else
          {
            ServerLogger.logger.LogError(()=>$"Failed to parse AFK time limit for {profile.Name}: {profile.AfkLimit}");
          }
        }
        byte[] packedBytes = InstructionsProtocol.SerializeInstructions(profile.Keys, afkSpan);
        Operation.OperationType newType = new(packedBytes, targetId);

        operationalSuite.Add(newType);
        profileMapping[targetId] = profile;

        ServerLogger.logger.LogInformation(()=>$"[Configured Profile] {profile.Name} bound to key hooks: [{string.Join(", ", profile.Keys)}]");
      }

      var server = new VotingServer(transportCluster, operationalSuite, userDB);

      server.OnClientConnected += (sender, clientId) => ServerLogger.logger.LogDebug(()=>$"[Cluster Joined] Client ID: {clientId}");
      server.OnClientHandshakeCompleted += (sender, clientId) => ServerLogger.logger.LogDebug(()=>$"[Handshake Verified] Client {clientId} initialized successfully.");
      server.OnClientDisconnected += (sender, e) => ServerLogger.logger.LogDebug(()=>$"[Cluster Left] Client ID: {e.ClientId}. Reason: {e.Reason}");
      server.OnClientAuthorized += (sender, e) => ServerLogger.logger.LogDebug(()=>$"[Authorizing] Client ID: {e.Client}. User: {e.User.Name}");

      var voteManager = new VotingManager(server);
      voteManager.OnOperationCountChanged += (sender, data) =>
      {
        var (type, voters, supporters) = data;

        // Match operation context against its parent profile layout configurations
        if (profileMapping.TryGetValue(type.Id, out var targetProfile))
        {
          // ROUTE MATRIX METRICS PROCESSING LOGIC TO RESULTPROCESSOR
          ResultProcessor.ProcessMetricUpdate(targetProfile, voters, supporters);
        }
      };

      var serverTask = server.StartAsync();

      Console.ForegroundColor = ConsoleColor.Cyan;
      Console.WriteLine($"\nServer active. Hosting TCP ({config.Network.TcpHost}:{config.Network.TcpPort}) and WebSockets ({sanitizedWsPrefix}) layers...");
      Console.ResetColor();

      bool readContinue = true;
      while(readContinue){
        string line = Console.ReadLine() ?? "";
        string[] command = line.Split(' ', StringSplitOptions.None);
        switch (command.FirstOrDefault(""))
        {
          case "":
            Console.WriteLine("you can use `help` to get help.");
            break;
          case "manager":
            UserDatabase.RunConsoleManager(userDB);
            break;
          case "exit":
            readContinue = false;
            break;
          case "loglvl":
            switch (command.ElementAtOrDefault(1))
            {
              case "debug":
                ServerLogger.CurrentLogLevel = LogLevel.Debug;
                break;
              case "information":
                ServerLogger.CurrentLogLevel = LogLevel.Information;
                break;
              case "warning":
                ServerLogger.CurrentLogLevel = LogLevel.Warning;
                break;
              case "error":
                ServerLogger.CurrentLogLevel = LogLevel.Error;
                break;
            }
            break;
          case "help":
            Console.WriteLine("exit           - shut down the server");
            Console.WriteLine("help           - get help");
            Console.WriteLine("loglvl <level> - set log level to:");
            Console.WriteLine("  debug/information/warning/error");
            Console.WriteLine("manager        - open user manager");
            break;
          default:
            Console.WriteLine($"Invalid command: {command}, you can use `help` to get help.");
            break;
        }
      }

      Console.WriteLine("Broadcasting closing frames and wrapping up operations...");
      await server.BroadcastShutdownSignalAsync();
      server.Stop();
      await serverTask;
      Console.WriteLine("Server is closed.");
    }
  }
}