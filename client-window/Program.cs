using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using operation_vote.Client;
using operation_vote.Client.Request;
using operation_vote.Interface.Shared;

namespace operation_vote.Interface.ClientWindow
{
  internal class Program
  {
    [STAThread]
    public static void Main(string[] args)
    {
      if (args.Length < 1)
      {
        Console.WriteLine("Usage: client-window <uri:(ip[:port])> [<protocol:tcp|ws|wss>(\"tcp\")] [<username:string?>(null:unauthorized)] [<password:string>(\"42\")]");
        return;
      }
      var uri = args[0];
      AuthenticationClient.AuthenticationData? authenticationData = null;
      if(args.Length >= 3)
      {
        string password = "42";
        if(args.Length >= 4) password=args[3];
        authenticationData = new(args[2], password);
        Console.WriteLine($"Logging in as {authenticationData.Username}");
      }
      if (args.ElementAtOrDefault(1) == "ws")
      {
        new WindowClient<SocketRequestHandlerWS>().Main(args, authenticationData, $"ws://{uri}/");
      }
      else if (args.ElementAtOrDefault(1) == "wss")
      {
        new WindowClient<SocketRequestHandlerWS>().Main(args, authenticationData, $"wss://{uri}/");
      }
      else
      {
        new WindowClient<SocketRequestHandlerTCP>().Main(args, authenticationData, uri);
      }
    }
  }

  public class WindowClient<T> where T : ISocketRequestHandler, new()
  {
    private CancellationTokenSource? _cts;
    private ISocketRequestHandler? _socketHandler;
    private VotingClient<T>? _client;

    // Thread-safe state tracking for the modern AFK logic
    private readonly ConcurrentDictionary<string, Operation.OperationType> _keyOpMapping = new();

    private ClientManager<T> ClientManager = null!;


    [STAThread]
    public void Main(string[] args, AuthenticationClient.AuthenticationData? authenticationData, string uri)
    {
      Console.WriteLine("=== STARTING VOTING CLIENT RUNTIME DOMAIN ===");

      _cts = new CancellationTokenSource();

      // Create the app builder setup
      var appBuilder = AppBuilder.Configure<Application>()
          .UsePlatformDetect()
          .LogToTrace()
          .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] })
          .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] });

      // We explicitly register to the framework setup callback
      appBuilder.AfterSetup(builder =>
      {
        if (builder.Instance?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
          // We intercept the framework's native startup to initialize our assets synchronously with it
          desktop.Startup += async (sender, startupArgs) =>
          {
            await InitializeAndShowUIAsync(uri, authenticationData, desktop);
          };

          // Hard shutdown handle to clean up background tasks instantly on exit
          desktop.ShutdownRequested += async(sender, e) =>
          {
            Console.WriteLine("UI Shutdown requested. Cleaning up core assets...");
            CleanupBackgroundTasks();
            _cts?.Cancel();
            _client?.DisposeAsync();
            _socketHandler?.Dispose();
          };
        }
      });

      // Start the application loop. Avalonia now knows it's managing a proper lifecycle.
      appBuilder.StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);

      // Final fallback cleanup when the loop breaks out
      CleanupBackgroundTasks();
      _cts?.Cancel();
      _client?.Dispose();
      _socketHandler?.Dispose();
      Console.WriteLine("=== RUNTIME DOMAIN EXITED ===");
    }

    private void CleanupBackgroundTasks() => ClientManager?.Dispose();

    private async Task InitializeAndShowUIAsync(string uri, AuthenticationClient.AuthenticationData? authenticationData, IClassicDesktopStyleApplicationLifetime desktop)
    {
      try
      {
        _cts ??= new CancellationTokenSource();
        T socketHandler = new();
        _socketHandler = socketHandler;
        _client = new VotingClient<T>(socketHandler, uri, _cts.Token)
        {
          authenticationData=authenticationData
        };
        ClientManager = ClientManager<T>.LaunchClientManager(_client);

        Console.WriteLine($"Connecting to network node at {uri}...");
        _client.OnAuthorizationFinished += (sender, success) =>
        {
          if(!success)
            Console.WriteLine("Failed to login, continuing as Anonymous.");
        };
        _client.OnUserChanged += (sender, user) =>
        {
          if(user != null)
            Console.WriteLine($"Logged in as {authenticationData?.Username}");
          else
            Console.WriteLine($"Logged out.");
        };
        socketHandler.OnDisconnected += (sender, reason) =>
        {
          var (_reason, isNormal) = reason();
          if(!isNormal)
            Console.WriteLine($"Connection is closed: {_reason}");
        };
        
        await _client.ConnectAsync();
        Console.WriteLine("Handshake completed. Server connection initialized successfully.");

        var window = new VotingWindow<T>(_client, ClientManager);
        desktop.MainWindow = window; 
        
        // Explicitly show the window inside the lifecycle loop
        window.Show();
      }
      catch (Exception ex)
      {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[Fatal Error] Network/UI Sync failed: {ex.Message}");
        // Console.WriteLine(ex.StackTrace);
        Console.ResetColor();
        
        // If the connection drops before initialization finishes, exit the app
        Avalonia.Threading.Dispatcher.UIThread.Post(() => 
        {
            desktop.Shutdown();
        });
      }
    }
  }
}