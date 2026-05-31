using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace operation_vote.Interface.Server
{
  // Polymorphic JSON layout targets
  [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
  [JsonDerivedType(typeof(PressKeyResultConfig), "PressKey")]
  [JsonDerivedType(typeof(OutputResultConfig), "Output")]
  public abstract record BaseResultConfig(
      [property: JsonPropertyName("requireSupportRate")] double RequireSupportRate
  );

  public record PressKeyResultConfig(
      [property: JsonPropertyName("key")] string Key,
      [property: JsonPropertyName("requireVoters")] int RequireVoters,
      [property: JsonPropertyName("requireSupporters")] int RequireSupporters,
      double RequireSupportRate
  ) : BaseResultConfig(RequireSupportRate);

  public record OutputResultConfig(
      [property: JsonPropertyName("id")] string Id,
      [property: JsonPropertyName("fd")] int Fd,
      double RequireSupportRate
  ) : BaseResultConfig(RequireSupportRate);

  public static class ResultProcessor
  {
    // Tracks state changes for Output rules ("ProfileName:RuleId" -> "+" or "-")
    private static readonly Dictionary<string, string> _outputStateCache = new();

    // Tracks state changes for PressKey rules ("ProfileName:Key" -> "Hold" or "Release")
    private static readonly Dictionary<string, string> _keyStateCache = new();

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
      bool matchesVoters = voters >= rule.RequireVoters;
      bool matchesSupporters = supporters >= rule.RequireSupporters;
      bool matchesRate = rate >= rule.RequireSupportRate;

      // Must satisfy ALL conditions to hold the key down
      string currentState = (matchesVoters && matchesSupporters && matchesRate) ? "Hold" : "Release";

      // Initialize or detect state transition
      if (!_keyStateCache.TryGetValue(stateKey, out string? lastState))
      {
        _keyStateCache[stateKey] = currentState;
        TriggerKeyAction(rule, currentState, rate);
        return;
      }

      if (currentState != lastState)
      {
        _keyStateCache[stateKey] = currentState;
        TriggerKeyAction(rule, currentState, rate);
      }
    }

    private static void EvaluateOutputRule(string profileName, OutputResultConfig rule, double rate)
    {
      string stateKey = $"{profileName}:{rule.Id}";

      // Calculate current state based on support rate threshold match
      bool thresholdMet = rate >= rule.RequireSupportRate;
      string currentState = thresholdMet ? "+" : "-";

      if (!_outputStateCache.TryGetValue(stateKey, out string? lastState))
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

    private static void TriggerKeyAction(PressKeyResultConfig rule, string action, double currentRate)
    {
      // Triggers exclusively on state flips: "Hold ' '" or "Release ' '"
      Console.ForegroundColor = action == "Hold" ? ConsoleColor.Green : ConsoleColor.Yellow;
      Console.WriteLine($"[Key Action] {action} Key: '{rule.Key}' (Support Rate: {currentRate:P1})");
      Console.ResetColor();

      bool isKeyDown = action == "Hold";
      KeyboardInjector.InjectKey(rule.Key, isKeyDown);
    }

    private static void TriggerOutputAction(OutputResultConfig rule, string state, double currentRate)
    {
      // Cleanly outputs standard "+jump\n" or "-jump\n" transitions without repetition spam
      Console.ForegroundColor = ConsoleColor.Cyan;
      Console.WriteLine($"[Output Action] {state}{rule.Id} (FD: {rule.Fd}, Support Rate: {currentRate:P1})");
      Console.ResetColor();

      // Direct stream writer writing execution block:
      if (rule.Fd == 2)
      {
        Console.Error.Write($"{state}{rule.Id}\n");
      }
      else
      {
        Console.Write($"{state}{rule.Id}\n");
      }
    }
  }
}