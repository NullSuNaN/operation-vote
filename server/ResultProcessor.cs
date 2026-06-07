using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace operation_vote.Interface.Server
{
  // Polymorphic JSON layout targets
  [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
  [JsonDerivedType(typeof(PressKeyResultConfig), "PressKey")]
  [JsonDerivedType(typeof(OutputResultConfig), "Output")]
  public abstract record BaseResultConfig(
      [property: JsonPropertyName("requireSupportRate")] double? RequireSupportRate
  );

  public record PressKeyResultConfig(
      [property: JsonPropertyName("key")] string Key,
      [property: JsonPropertyName("requireVoters")] int? RequireVoters,
      [property: JsonPropertyName("requireSupporters")] int? RequireSupporters,
      double? RequireSupportRate
  ) : BaseResultConfig(RequireSupportRate);

  public record OutputResultConfig(
      [property: JsonPropertyName("id")] string Id,
      [property: JsonPropertyName("fd")] int Fd,
      double? RequireSupportRate
  ) : BaseResultConfig(RequireSupportRate);

  public static class ResultProcessor
  {
    // Tracks state changes for Output rules ("ProfileName:RuleId" -> "+" or "-")
    private static readonly Dictionary<string, bool> _outputStateCache = new();

    // Tracks state changes for PressKey rules ("ProfileName:Key" -> "Hold" or "Release")
    private static readonly Dictionary<string, bool> _keyStateCache = new();

    /// <summary>
    /// Evaluates active runtime state parameters against a profile's defined voting rules matrix.
    /// </summary>
    public static void ProcessMetricUpdate(ProfileConfig profile, int totalVoters, int totalSupporters)
    {
      if (profile.VoteResults == null) return;

      double currentRate = totalVoters > 0 ? (double)totalSupporters / totalVoters : 0.0;

      foreach (var result in profile.VoteResults)
      {
        if (result is PressKeyResultConfig keyRule)
        {
          EvaluatePressKeyRule(profile.Name, keyRule, totalVoters, totalSupporters, currentRate);
        }
        else if (result is OutputResultConfig outputRule)
        {
          EvaluateOutputRule(profile.Name, outputRule, currentRate);
        }
      }
    }

    private static void EvaluatePressKeyRule(string profileName, PressKeyResultConfig rule, int voters, int supporters, double rate)
    {
      string stateKey = $"{profileName}:{rule.Key}";

      // Evaluate all target conditions for a Hold
      bool matchesVoters = CheckRule(voters, rule.RequireVoters);
      bool matchesSupporters = CheckRule(supporters, rule.RequireSupporters);
      bool matchesRate = CheckRule(rate, rule.RequireSupportRate);

      // Must satisfy ALL conditions to hold the key down
      bool currentState = matchesVoters && matchesSupporters && matchesRate;

      // Initialize or detect state transition
      if (!_keyStateCache.TryGetValue(stateKey, out bool lastState))
      {
        _keyStateCache[stateKey] = currentState;
        TriggerKeyAction(rule, currentState, (voters, supporters, rate));
        return;
      }

      if (currentState != lastState)
      {
        _keyStateCache[stateKey] = currentState;
        TriggerKeyAction(rule, currentState, (voters, supporters, rate));
      }
    }
    private static bool CheckRule<T>(T value, T? requirement) where T : struct, INumber<T>
    {
      if (requirement == null) return true;
      if (requirement.Value < T.Zero) return value <= requirement.Value;
      return value >= requirement.Value;
    }

    private static void EvaluateOutputRule(string profileName, OutputResultConfig rule, double rate)
    {
      string stateKey = $"{profileName}:{rule.Id}";

      // Calculate current state based on support rate threshold match
      bool thresholdMet = rate >= rule.RequireSupportRate;
      bool currentState = thresholdMet;

      if (!_outputStateCache.TryGetValue(stateKey, out bool lastState))
      {
        _outputStateCache[stateKey] = currentState;
        TriggerOutputAction(rule, currentState, rate);
        return;
      }

      if (currentState != lastState)
      {
        _outputStateCache[stateKey] = currentState;
        TriggerOutputAction(rule, currentState, rate);
      }
    }

    private static void TriggerKeyAction(PressKeyResultConfig rule, bool action, (int Voters, int Supporters, double Rate) stat)
    {
      // Triggers exclusively on state flips: "Hold ' '" or "Release ' '"
      Console.ForegroundColor = action ? ConsoleColor.Green : ConsoleColor.Yellow;
      ServerLogger.logger.LogInformation(()=>$"[Key Action] {action} Key: '{rule.Key}' ({stat.Supporters}/{stat.Voters}, {stat.Rate:P2})");
      Console.ResetColor();

      bool isKeyDown = action;
      KeyboardInjector.InjectKey(rule.Key, isKeyDown);
    }
    private static void TriggerOutputAction(OutputResultConfig rule, bool state, double currentRate)
    {
      try
      {
        // 1. Construct the exact payload string format (e.g., "+jump\n" or "-jump\n")
        string actionPrefix = state ? "+" : "-";
        string wirePayload = $"{actionPrefix}{rule.Id}\n";
        byte[] payloadBytes = Encoding.UTF8.GetBytes(wirePayload);

        SafeFileHandle? safeHandle = null;

        // 2. Cross-platform descriptor handle resolution
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
          // Windows-specific handle assignment
          safeHandle = new SafeFileHandle(rule.Fd, ownsHandle: false);
        }
        else
        {
          // POSIX: Directly wrap the raw POSIX file descriptor integer handle.
          // Setting ownsHandle to false is critical to prevent the stream from closing 
          // the main terminal stdout/stderr pipelines on disposal.
          safeHandle = new SafeFileHandle(rule.Fd, ownsHandle: false);
        }

        // 3. Open an unbuffered direct stream writer block targeting the native handle
        using (safeHandle)
        using (var customFdStream = new FileStream(safeHandle, FileAccess.Write))
        {
          customFdStream.Write(payloadBytes, 0, payloadBytes.Length);
          customFdStream.Flush();
        }
      }
      catch (Exception ex)
      {
        // Suppress or log errors if the target descriptor pipe has broken or has been closed by the host environment
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"[Pipeline Warning] Failed to write to cross-platform target FD {rule.Fd}: {ex.Message}");
        Console.ResetColor();
      }
    }
  }
}