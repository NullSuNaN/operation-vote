using System.Text;
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
    [property: JsonPropertyName("TcpHost")] string? TcpHost,
    [property: JsonPropertyName("TcpPort")] int? TcpPort,
    [property: JsonPropertyName("WsUriPrefix")] string? WsUriPrefix
  );

  public record ProfileConfig(
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("Keys")] string[] Keys,
    [property: JsonPropertyName("VoteResults")] List<BaseResultConfig> VoteResults,
    [property: JsonPropertyName("AfkLimit")] string? AfkLimit
  );

  public record LoggingConfig(
    [property: JsonPropertyName("LogLevel")] string LogLevel,
    [property: JsonPropertyName("LogNetworkTrace")] bool? LogNetworkTrace
  );

  public record ServerAppConfig(
    [property: JsonPropertyName("Network")] NetworkConfig Network,
    [property: JsonPropertyName("Alert")] bool? Alert,
    [property: JsonPropertyName("Profiles")] List<ProfileConfig> Profiles,
    [property: JsonPropertyName("Logging")] LoggingConfig? Logging
  );

  internal class Program
  {
    private static long _idCounter = 0;
    public static long NewId => Interlocked.Increment(ref _idCounter);
    public static UserDatabase userDB = null!;
    public static string programName = Environment.GetCommandLineArgs().FirstOrDefault("server");
    public static async Task<int> Main(string[] args)
    {
      userDB = [];
      string? first = args.FirstOrDefault((string?)null);
      string configFile = "config.json";
      bool runManager = false;
      if (first?.StartsWith("--") ?? false)
      {
        if (first.Length == 2)
          configFile = args.ElementAtOrDefault(1) ?? "config.json";
        else if (first == "--manager")
        {
          runManager = true;
        }
        else if (first == "--help")
        {
          ShowHelp();
          return 0;
        }
        else
        {
          Console.WriteLine($"Invalid option: {first}");
          Console.WriteLine($"Use {programName} --help to get help");
          return 1;
        }
      }
      else configFile = first ?? configFile;
      string configPath = Path.Combine(Environment.CurrentDirectory, configFile);

      if (!File.Exists(configPath))
      {
        Console.ForegroundColor = ConsoleColor.Red;
        ServerLogger.logger.LogInformation(() => $"[Fatal Error] Configuration layout target missing at: {configPath}");
        Console.ResetColor();
        return 0;
      }

      try
      {
        ServerLogger.logger.LogInformation(() => "=== INITIALIZING ===");
        ServerLogger.logger.LogInformation(() => $"Loading configuration rules from: {configPath}");

        string jsonText = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize<ServerAppConfig>(jsonText);

        if (config == null || config.Network == null || config.Profiles == null)
        {
          ServerLogger.logger.LogInformation(() => "[Error] Serialization failed: Root metadata target fields are null.");
          return 1;
        }

        if (config.Alert ?? false)
          Console.Write('\a');

        if (runManager)
          UserDatabase.RunConsoleManager();
        else
          await RunServerWithConfigAsync(config);
      }
      catch (Exception ex)
      {
        Console.ForegroundColor = ConsoleColor.Red;
        ServerLogger.logger.LogInformation(() => $"[Fatal System Crash] {ex.Message}");
        if (ex.InnerException != null)
        {
          ServerLogger.logger.LogInformation(() => $"Details: {ex.InnerException.Message}");
        }
        ServerLogger.logger.LogTrace(() => ex.StackTrace);
        Console.ResetColor();
      }
      return 0;
    }

    private static async Task RunServerWithConfigAsync(ServerAppConfig config)
    {
      var loggingConfig = config.Logging;
      bool LogNetworkTrace = loggingConfig?.LogNetworkTrace ?? false;
      if (loggingConfig != null)
      {
        if(GetLogLevel(loggingConfig.LogLevel) is LogLevel level)
          ServerLogger.CurrentLogLevel = level;
      }

      // 1. TCP Driver Binds Directly
      var tcpDriver = (string.IsNullOrEmpty(config.Network.TcpHost) || config.Network.TcpPort is null)
          ? null
          : new TcpServerChannel(config.Network.TcpHost, config.Network.TcpPort!.Value);

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
      if (tcpDriver != null) transportCluster.Add(tcpDriver);
      if (wsDriver != null) transportCluster.Add(wsDriver);
      if(LogNetworkTrace)
        foreach (var cluster in transportCluster)
        {
          cluster.OnChannelDataReceived += (sender, e) =>
          {
            ServerLogger.logger.LogTrace(() => $"[Network] {e.Client.ClientId} -> server: {Encoding.UTF8.GetString(e.Payload)} |{string.Concat(e.Payload.Select(b => ' '+b.ToString()))}");
          };
          cluster.OnChannelDataSent += (sender, e) =>
          {
            ServerLogger.logger.LogTrace(() => $"[Network] server -> {e.Client.ClientId}: {Encoding.UTF8.GetString(e.Payload)} |{string.Concat(e.Payload.Select(b => ' '+b.ToString()))}");
          };
        }

      var operationalSuite = new List<Operation.OperationType>();

      // Store profiles using internal tracking IDs to link execution updates accurately
      var profileMapping = new Dictionary<long, ProfileConfig>();

      foreach (var profile in config.Profiles)
      {
        long targetId = NewId;
        TimeSpan? afkSpan = null;
        if (profile.AfkLimit != null)
        {
          if (TimeSpan.TryParse(profile.AfkLimit.AsSpan(), out var _afkSpan))
          {
            afkSpan = _afkSpan;
            ServerLogger.logger.LogInformation(() => $"Parsed AFK time limit for {profile.Name}: {profile.AfkLimit} as {_afkSpan}");
          }
          else
          {
            ServerLogger.logger.LogError(() => $"Failed to parse AFK time limit for {profile.Name}: {profile.AfkLimit}");
          }
        }
        byte[] packedBytes = InstructionsProtocol.SerializeInstructions(profile.Keys, afkSpan);
        Operation.OperationType newType = new(packedBytes, targetId);

        operationalSuite.Add(newType);
        profileMapping[targetId] = profile;

        ServerLogger.logger.LogInformation(() => $"[Configured Profile] {profile.Name} bound to key hooks: [{string.Join(", ", profile.Keys)}]");
      }

      var server = new VotingServer(transportCluster, operationalSuite, userDB);

      server.OnClientConnected += (sender, client) => ServerLogger.logger.LogDebug(() => $"[Cluster Joined] Client ID: {client.ClientId}");
      server.OnClientHandshakeCompleted += (sender, client) => ServerLogger.logger.LogDebug(() => $"[Handshake Verified] Client {client.ClientId} initialized successfully.");
      server.OnClientDisconnected += (sender, e) => ServerLogger.logger.LogDebug(() => $"[Cluster Left] Client ID: {e.Client.ClientId}. Reason: {e.Reason}");
      server.OnClientAuthorized += (sender, e) => ServerLogger.logger.LogDebug(() => $"[Authorizing] Client ID: {e.Client.ClientId}. User: {e.User.Name}");

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
      while (readContinue)
      {
        string line = Console.ReadLine() ?? "";
        string[] command = line.Split(' ', StringSplitOptions.None);
        switch (command.FirstOrDefault(""))
        {
          case "":
            Console.WriteLine("you can use `help` to get help.");
            break;
          case "manager":
            UserDatabase.RunConsoleManager(userDB, server);
            break;
          case "exit":
            readContinue = false;
            break;
          case "loglvl":
            if(GetLogLevel(command.ElementAtOrDefault(1)) is LogLevel level)
              ServerLogger.CurrentLogLevel = level;
            else
              Console.WriteLine($"Current Level: {ServerLogger.CurrentLogLevel}");
            break;
          case "help":
            Console.WriteLine("exit           - shut down the server");
            Console.WriteLine("help           - get help");
            Console.WriteLine("loglvl <level> - set log level to:");
            Console.WriteLine("  trace/debug/information/warning/error");
            Console.WriteLine("manager        - open user manager");
            break;
          default:
            Console.WriteLine($"Invalid command: {command.FirstOrDefault()}, you can use `help` to get help.");
            break;
        }
      }

      Console.WriteLine("Broadcasting closing frames and wrapping up operations...");
      await server.BroadcastShutdownSignalAsync();
      server.Stop();
      await serverTask;
      Console.WriteLine("Server is closed.");
    }
    static void ShowHelp()
    {
      Console.WriteLine($"{programName} [<config-file>:config.json]");
      Console.WriteLine($"{programName} -- <config-file>");
      Console.WriteLine($"{programName} --manager");
    }
    static LogLevel? GetLogLevel(string? str)
    {
      return str?.Trim()?.ToLower() switch
      {
        "trace" => LogLevel.Trace,
        "trce" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "debg" => LogLevel.Debug,
        "information" => LogLevel.Information,
        "info" => LogLevel.Information,
        "warning" => LogLevel.Warning,
        "warn" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "err" => LogLevel.Error,
        _ => null,
      };
    }
  }
}