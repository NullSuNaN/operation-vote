using Microsoft.Extensions.Logging;

namespace operation_vote.Interface.Server
{
  public class ServerLogger
  {
    private static readonly ILoggerFactory factory = LoggerFactory.Create(builder =>
      {
        builder.AddConsole(); // Output to standard console windows
        builder.AddDebug();   // Output to the IDE debugging pipeline
        builder.SetMinimumLevel(LogLevel.Information);
      });
    private ServerLogger() { }
    internal static readonly ILogger<Program> logger = factory.CreateLogger<Program>();
    public readonly static ServerLogger Instance = new();
  }
  public static class LoggerExtensions
  {
    /// <summary>
    /// Core centralized log router. All specific level extensions pass through here.
    /// </summary>
    public static void Log(this ILogger logger, LogLevel logLevel, Func<string?> messageFactory)
    {
      // 1. Guard against LogLevel.None instantly to avoid execution allocations
      if (logLevel == LogLevel.None) return;

      // 2. Evaluate if the channel is open to receiving this tier of event data
      if (logger.IsEnabled(logLevel))
      {
        // 3. Evaluate the lambda delegate statement only when strictly required
        string? message = messageFactory();
        if (message != null)
        {
#pragma warning disable CA2254 // The message argument to ILogger.Log should be a constant string literal
          logger.Log(logLevel, message);
#pragma warning restore CA2254
        }
      }
    }

    public static void LogTrace(this ILogger logger, Func<string?> messageFactory) =>
      logger.Log(LogLevel.Trace, messageFactory);

    public static void LogDebug(this ILogger logger, Func<string?> messageFactory) =>
      logger.Log(LogLevel.Debug, messageFactory);

    public static void LogInformation(this ILogger logger, Func<string?> messageFactory) =>
      logger.Log(LogLevel.Information, messageFactory);

    public static void LogWarning(this ILogger logger, Func<string?> messageFactory) =>
      logger.Log(LogLevel.Warning, messageFactory);

    public static void LogError(this ILogger logger, Func<string?> messageFactory) =>
      logger.Log(LogLevel.Error, messageFactory);

    public static void LogCritical(this ILogger logger, Func<string?> messageFactory) =>
      logger.Log(LogLevel.Critical, messageFactory);
  }
}