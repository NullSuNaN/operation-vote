using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
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
    [property: JsonPropertyName("VoteResults")] List<BaseResultConfig> VoteResults
  );

  public record ServerAppConfig(
    [property: JsonPropertyName("Network")] NetworkConfig Network,
    [property: JsonPropertyName("Profiles")] List<ProfileConfig> Profiles
  );

  internal class Program
  {
    private static long _idCounter = 0;
    public static long NewId => Interlocked.Increment(ref _idCounter);
    public static async Task Main(string[] args)
    {
      string configPath = Path.Combine(AppContext.BaseDirectory, args.FirstOrDefault("config.json"));

      if (!File.Exists(configPath))
      {
        Console.ForegroundColor = ConsoleColor.Red;
        ServerLogger.logger.LogInformation(()=>$"[Fatal Error] Configuration layout target missing at: {configPath}");
        Console.ResetColor();
        return;
      }

      try
      {
        ServerLogger.logger.LogInformation(()=>"=== INITIALIZING MULTI-PROTOCOL HYBRID VOTING ENGINE ===");
        ServerLogger.logger.LogInformation(()=>$"Loading configuration rules from: {configPath}");

        string jsonText = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize<ServerAppConfig>(jsonText);

        if (config == null || config.Network == null || config.Profiles == null)
        {
          ServerLogger.logger.LogInformation(()=>"[Error] Serialization failed: Root metadata target fields are null.");
          return;
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

      var transportCluster = new List<IServerChannel>();
      if (tcpDriver != null) transportCluster.Add(tcpDriver);
      if (wsDriver != null) transportCluster.Add(wsDriver);

      var operationalSuite = new List<Operation.OperationType>();

      // Store profiles using internal tracking IDs to link execution updates accurately
      var profileMapping = new Dictionary<long, ProfileConfig>();

      foreach (var profile in config.Profiles)
      {
        long targetId = NewId;
        byte[] packedBytes = PackKeysToBinary(profile.Keys);
        Operation.OperationType newType = new(packedBytes, targetId);

        operationalSuite.Add(newType);
        profileMapping[targetId] = profile;

        ServerLogger.logger.LogInformation(()=>$"[Configured Profile] {profile.Name} bound to key hooks: [{string.Join(", ", profile.Keys)}]");
      }

      var server = new VotingServer(transportCluster, operationalSuite);

      server.OnClientConnected += (sender, clientId) => ServerLogger.logger.LogDebug(()=>$"[Cluster Joined] Client ID: {clientId}");
      server.OnClientHandshakeCompleted += (sender, clientId) => ServerLogger.logger.LogDebug(()=>$"[Handshake Verified] Client {clientId} initialized successfully.");
      server.OnClientDisconnected += (sender, e) => ServerLogger.logger.LogDebug(()=>$"[Cluster Left] Client ID: {e.ClientId}. Reason: {e.Reason}");

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
      Console.WriteLine($"\nServer active. Hosting TCP ({config.Network.TcpPort}) and WebSockets ({sanitizedWsPrefix}) layers...");
      Console.ResetColor();
      Console.WriteLine("Press [ENTER] to shut down the server...\n");
      Console.ReadLine();

      Console.WriteLine("Broadcasting closing frames and wrapping up operations...");
      await server.BroadcastShutdownSignalAsync();
      server.Stop();
      await serverTask;
      Console.WriteLine("Server is closed.");
    }

    private static byte[] PackKeysToBinary(string[] instructions)
    {
      using var ms = new MemoryStream();
      using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
      {
        writer.Write((char[])[.. "keys:"]);
        foreach (var str in instructions)
        {
          writer.Write(str);
        }
      }
      return ms.ToArray();
    }
  }
}