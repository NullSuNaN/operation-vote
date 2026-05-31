using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using operation_vote.Client;
using operation_vote.Client.Request;
using operation_vote.Shared;

namespace operation_vote.Interface.ClientWindow
{
  internal class Program
  {
    // FIX: Keep the network handlers in static scope so they are never garbage collected or disposed early
    private static CancellationTokenSource? _cts;
    private static SocketRequestHandlerTCP? _tcpHandler;
    private static VotingClient<SocketRequestHandlerTCP>? _client;

    [STAThread]
    public static void Main(string[] args)
    {
      if (args.Length < 1)
      {
        Console.WriteLine("Usage: client-console <uri>");
        return;
      }
      var uri = args[0];

      Console.WriteLine("=== STARTING VOTING CLIENT RUNTIME DOMAIN ===");

      BuildAvaloniaApp(uri).StartWithClassicDesktopLifetime(args);

      // Clean up when the window application closes entirely
      _cts?.Cancel();
      _client?.Dispose();
      _tcpHandler?.Dispose();
    }
    public static AppBuilder BuildAvaloniaApp(string serverUri)
    {
      return AppBuilder.Configure<Application>()
          .UsePlatformDetect()
          .LogToTrace()
          .With(new X11PlatformOptions
          {
            // Forces Avalonia to bypass hardware window compositing issues over WSL
            RenderingMode = [X11RenderingMode.Software]
          })
          .With(new Win32PlatformOptions
          {
            // Keeps native desktop operations stable if run natively outside WSL
            RenderingMode = [Win32RenderingMode.Software]
          })
          .AfterSetup(__ =>
          {
            _ = InitializeNetworkAndWindowAsync(serverUri);
          });
    }

    private static async Task InitializeNetworkAndWindowAsync(string uri)
    {
      try
      {
        _cts = new CancellationTokenSource();
        _tcpHandler = new SocketRequestHandlerTCP();
        _client = new VotingClient<SocketRequestHandlerTCP>(_tcpHandler, uri, _cts.Token);

        Console.WriteLine($"Connecting to network node at {uri}...");
        await _client.ConnectAsync();
        Console.WriteLine("Handshake completed. Server connection initialized successfully.");

        var availableOpTypes = _client.OperationTypes.Values.ToList();
        Console.WriteLine($"Discovered {availableOpTypes.Count} active operational profiles from server.");

        Dictionary<string, Operation.OperationType> keyOp = [];

        foreach (var opType in availableOpTypes)
        {
          if (opType?.Instructions == null || opType.Instructions.Length == 0) continue;

          var instructionsStrings = new List<string>();
          using var ms = new MemoryStream(opType.Instructions);
          using var reader = new BinaryReader(ms, Encoding.UTF8);

          // Discard "keys:" header prefix safely
          if (ms.Length >= 5)
          {
            byte[] headerBytes = reader.ReadBytes(5);
            string header = Encoding.UTF8.GetString(headerBytes);
            if (header != "keys:")
            {
              ms.Position = 0;
            }
          }

          while (ms.Position < ms.Length)
          {
            instructionsStrings.Add(reader.ReadString());
          }

          foreach (string key in instructionsStrings)
          {
            if (!string.IsNullOrEmpty(key))
            {
              keyOp[key] = opType;
            }
          }
        }

        // Dispatch UI window creation back to the primary thread safely
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
          var window = new VotingWindow<SocketRequestHandlerTCP>(_client, keyOp);
          window.Show();
        });
      }
      catch (Exception ex)
      {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[Fatal Error] Network/UI Sync failed: {ex.Message}");
        Console.Write(ex.StackTrace);
        Console.ResetColor();
      }
    }
  }
}