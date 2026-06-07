using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using operation_vote.Client;
using operation_vote.Client.Request;
using operation_vote.Shared;
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
        Console.WriteLine("Usage: client-window <uri:(ip[:port])> [<protocol:tcp|ws|wss>(tcp)] [<username:string?>(null:unauthorized)] [<password:string>(42)]");
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
    private ISocketRequestHandler? _tcpHandler;
    private VotingClient<T>? _client;

    // Thread-safe state tracking for the modern AFK logic
    private readonly ConcurrentDictionary<string, Operation.OperationType> _keyOpMapping = new();
    private long _lastClickTicks = DateTime.UtcNow.Ticks;
    private SortedList<TimeSpan, Operation.OperationType>? _timeouts = null;
    private readonly SemaphoreSlim _afkSignal = new(0, 1);
    private readonly ReaderWriterLockSlim _afkLock = new();
    private Task? _afkLoopTask;
    private CancellationTokenSource? _afkCts;

    [STAThread]
    public void Main(string[] args, AuthenticationClient.AuthenticationData? authenticationData, string uri)
    {
      Console.WriteLine("=== STARTING VOTING CLIENT RUNTIME DOMAIN ===");

      _cts = new CancellationTokenSource();
      _afkCts = new CancellationTokenSource();
      _afkLoopTask = AFKProcessingThreadAsync(_afkCts.Token);

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
          desktop.ShutdownRequested += (sender, e) =>
          {
            Console.WriteLine("UI Shutdown requested. Cleaning up core assets...");
            CleanupBackgroundTasks();
            _cts?.Cancel();
            _client?.Dispose();
            _tcpHandler?.Dispose();
          };
        }
      });

      // Start the application loop. Avalonia now knows it's managing a proper lifecycle.
      appBuilder.StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);

      // Final fallback cleanup when the loop breaks out
      CleanupBackgroundTasks();
      _cts?.Cancel();
      _client?.Dispose();
      _tcpHandler?.Dispose();
      Console.WriteLine("=== RUNTIME DOMAIN EXITED ===");
    }

    private void CleanupBackgroundTasks()
    {
      try { _afkCts?.Cancel(); } catch { /* no-op */ }
      try { _afkCts?.Dispose(); } catch { /* no-op */ }
      try { _afkSignal?.Dispose(); } catch { /* no-op */ }
      try { _afkLock?.Dispose(); } catch { /* no-op */ }
    }

    public void TrackUserActivity()
    {
      Interlocked.Exchange(ref _lastClickTicks, DateTime.UtcNow.Ticks);
      try { _afkSignal.Release(); } catch { /* no-op */ }
    }

    private async Task AFKProcessingThreadAsync(CancellationToken token)
    {
      while (!token.IsCancellationRequested)
      {
        SortedList<TimeSpan, Operation.OperationType>? localTimeouts;
        long clickTicks;

        _afkLock.EnterReadLock();
        try
        {
          localTimeouts = _timeouts;
          clickTicks = Volatile.Read(ref _lastClickTicks);
        }
        finally
        {
          _afkLock.ExitReadLock();
        }

        if (localTimeouts == null)
        {
          try { await _afkSignal.WaitAsync(token); } catch (OperationCanceledException) { break; }
          continue;
        }

        DateTime lastClickTime = new(Volatile.Read(ref clickTicks), DateTimeKind.Utc);
        TimeSpan lowestWait = TimeSpan.MaxValue;
        bool executePulse = false;
        Operation.OperationType? targetedOp = null;

        foreach (var item in localTimeouts)
        {
          TimeSpan calculatedDeadline = item.Key;
          TimeSpan elapsed = DateTime.UtcNow - lastClickTime;
          TimeSpan remainingWait = calculatedDeadline - elapsed;

          if (remainingWait <= TimeSpan.Zero)
          {
            executePulse = true;
            targetedOp = item.Value;
            break;
          }
          else if (remainingWait < lowestWait)
          {
            lowestWait = remainingWait;
          }
        }

        if (executePulse && targetedOp != null)
        {
          var activeClient = _client;
          if (activeClient != null)
          {
            try
            {
              using var afkOp = new Operation(targetedOp, VoteType.Abstain, Array.Empty<byte>());
              await activeClient.SendOperationAsync(afkOp, token);
            }
            catch { /* Handle drop errors contextually */ }
          }
          try { await Task.Delay(1000, token); } catch (OperationCanceledException) { break; }
        }
        else
        {
          int waitMilliseconds = lowestWait == TimeSpan.MaxValue ? -1 : (int)lowestWait.TotalMilliseconds;
          try
          {
            await _afkSignal.WaitAsync(waitMilliseconds, token);
          }
          catch (OperationCanceledException)
          {
            break;
          }
        }
      }
    }

    private async Task InitializeAndShowUIAsync(string uri, AuthenticationClient.AuthenticationData? authenticationData, IClassicDesktopStyleApplicationLifetime desktop)
    {
      try
      {
        _cts ??= new CancellationTokenSource();
        T tcpHandler = new();
        _tcpHandler = tcpHandler;
        _client = new VotingClient<T>(tcpHandler, uri, _cts.Token)
        {
          authenticationData=authenticationData
        };

        Console.WriteLine($"Connecting to network node at {uri}...");
        _client.OnAuthorizationFinished += (sender, success) =>
        {
          if(success)
            Console.WriteLine($"Logged in as {authenticationData?.Username}");
          else
            Console.WriteLine("Failed to login, continuing as Anonymous.");
        };
        await _client.ConnectAsync();
        Console.WriteLine("Handshake completed. Server connection initialized successfully.");

        var availableOpTypes = _client.OperationTypes.Values.ToList();
        var parsedTimeouts = new SortedList<TimeSpan, Operation.OperationType>();

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

        _afkLock.EnterWriteLock();
        try
        {
          _timeouts = parsedTimeouts;
        }
        finally
        {
          _afkLock.ExitWriteLock();
        }

        Interlocked.Exchange(ref _lastClickTicks, DateTime.UtcNow.Ticks);
        try { _afkSignal.Release(); } catch { /* no-op */ }

        var window = new VotingWindow<T>(_client, _keyOpMapping, TrackUserActivity);
        desktop.MainWindow = window; 
        
        // Explicitly show the window inside the lifecycle loop
        window.Show();
      }
      catch (Exception ex)
      {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[Fatal Error] Network/UI Sync failed: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
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