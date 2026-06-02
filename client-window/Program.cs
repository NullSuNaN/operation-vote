using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using operation_vote.Client;
using operation_vote.Client.Request;

namespace operation_vote.Interface.ClientWindow
{
  internal class Program
  {
    [STAThread]
    public static void Main(string[] args)
    {
      if (args.Length < 1)
      {
        Console.WriteLine("Usage: client-console <uri:(ip[:port])> [<protocol:tcp|ws|wss>(tcp)]");
        return;
      }
      var uri = args[0];
      if (args.ElementAtOrDefault(1) == "ws")
      {
        new WindowClient<SocketRequestHandlerWS>().Main(args, $"ws://{uri}/");
      }
      else if (args.ElementAtOrDefault(1) == "wss")
      {
        new WindowClient<SocketRequestHandlerWS>().Main(args, $"wss://{uri}/");
      }
      else
      {
        new WindowClient<SocketRequestHandlerTCP>().Main(args, uri);
      }
    }
  }

  public class WindowClient<T> where T : ISocketRequestHandler, new()
  {
    private CancellationTokenSource? _cts;
    private ISocketRequestHandler? _tcpHandler;
    private VotingClient<T>? _client;

    [STAThread]
    public void Main(string[] args, string uri)
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
            await InitializeAndShowUIAsync(uri, desktop);
          };

          // Hard shutdown handle to clean up background tasks instantly on exit
          desktop.ShutdownRequested += (sender, e) =>
          {
            Console.WriteLine("UI Shutdown requested. Cleaning up core assets...");
            _cts?.Cancel();
            _client?.Dispose();
            _tcpHandler?.Dispose();
          };
        }
      });

      // Start the application loop. Avalonia now knows it's managing a proper lifecycle.
      appBuilder.StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);

      // Final fallback cleanup when the loop breaks out
      _cts?.Cancel();
      _client?.Dispose();
      _tcpHandler?.Dispose();
      Console.WriteLine("=== RUNTIME DOMAIN EXITED ===");
    }

    private async Task InitializeAndShowUIAsync(string uri, IClassicDesktopStyleApplicationLifetime desktop)
    {
      try
      {
        _cts ??= new CancellationTokenSource();
        T tcpHandler = new();
        _tcpHandler = tcpHandler;
        _client = new VotingClient<T>(tcpHandler, uri, _cts.Token);

        Console.WriteLine($"Connecting to network node at {uri}...");
        await _client.ConnectAsync();
        Console.WriteLine("Handshake completed. Server connection initialized successfully.");

        var availableOpTypes = _client.OperationTypes.Values.ToList();
        Dictionary<string, Operation.OperationType> keyOp = [];

        foreach (var opType in availableOpTypes)
        {
          if (opType?.Instructions == null || opType.Instructions.Length == 0) continue;

          var instructionsStrings = new List<string>();
          using var ms = new MemoryStream(opType.Instructions);
          using var reader = new BinaryReader(ms, Encoding.UTF8);

          if (ms.Length >= 5)
          {
            byte[] headerBytes = reader.ReadBytes(5);
            string header = Encoding.UTF8.GetString(headerBytes);
            if (header != "keys:") ms.Position = 0;
          }

          while (ms.Position < ms.Length)
          {
            instructionsStrings.Add(reader.ReadString());
          }

          foreach (string key in instructionsStrings)
          {
            if (!string.IsNullOrEmpty(key)) keyOp[key] = opType;
          }
        }

        // --- THE CRITICAL LIFETIME FIX ---
        // We initialize the window and assign it directly as Avalonia's managed MainWindow
        var window = new VotingWindow<T>(_client, keyOp);
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