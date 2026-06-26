using System.Runtime.Versioning;
using Avalonia.Input;

namespace operation_vote.Interface.ClientWindow
{
  public static class KeyMappingExtensions
  {
    /// <summary>
    /// Converts an Avalonia KeyEventArgs infrastructure frame into its equivalent JavaScript style 'event.key' string.
    /// </summary>
    public static string GetJsStyleKeyName(KeyEventArgs e)
    {
      // 1. Identify if a Shift modifier variant is physically engaged
      bool isShiftPressed = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

      // 2. Evaluate and normalize unique structural and control hardware frames
      switch (e.Key)
      {
        // Structural Whitespace / Action Handles
        case Key.Space: return " ";
        case Key.Return: return "Enter";
        case Key.Tab: return "Tab";
        case Key.Escape: return "Escape";
        case Key.Back: return "Backspace";
        case Key.Delete: return "Delete";

        // Directional Navigation
        case Key.Up: return "ArrowUp";
        case Key.Down: return "ArrowDown";
        case Key.Left: return "ArrowLeft";
        case Key.Right: return "ArrowRight";
        case Key.Home: return "Home";
        case Key.End: return "End";
        case Key.PageUp: return "PageUp";
        case Key.PageDown: return "PageDown";

        // Functional Modifier Toggles
        case Key.LeftShift:
        case Key.RightShift: return "Shift";
        case Key.LeftCtrl:
        case Key.RightCtrl: return "Control";
        case Key.LeftAlt:
        case Key.RightAlt: return "Alt";

        // Top-Row Numerical Array Mapping (D0 - D9)
        case Key.D0: return isShiftPressed ? ")" : "0";
        case Key.D1: return isShiftPressed ? "!" : "1";
        case Key.D2: return isShiftPressed ? "@" : "2";
        case Key.D3: return isShiftPressed ? "#" : "3";
        case Key.D4: return isShiftPressed ? "$" : "4";
        case Key.D5: return isShiftPressed ? "%" : "5";
        case Key.D6: return isShiftPressed ? "^" : "6";
        case Key.D7: return isShiftPressed ? "&" : "7";
        case Key.D8: return isShiftPressed ? "*" : "8";
        case Key.D9: return isShiftPressed ? "(" : "9";

        // Common Punctuation & Symbolic Fallbacks
        case Key.OemMinus: return isShiftPressed ? "_" : "-";
        case Key.OemPlus: return isShiftPressed ? "+" : "=";
        case Key.OemOpenBrackets: return isShiftPressed ? "{" : "[";
        case Key.OemCloseBrackets: return isShiftPressed ? "}" : "]";
        case Key.OemSemicolon: return isShiftPressed ? ":" : ";";
        case Key.OemQuotes: return isShiftPressed ? "\"" : "'";
        case Key.OemComma: return isShiftPressed ? "<" : ",";
        case Key.OemPeriod: return isShiftPressed ? ">" : ".";
        case Key.OemQuestion: return isShiftPressed ? "?" : "/";
        case Key.OemPipe: return isShiftPressed ? "|" : "\\";
        case Key.OemTilde: return isShiftPressed ? "~" : "`";
      }

      // 3. Fallback processing for standard Alphanumeric systems (A-Z)
      string rawKeyName = e.Key.ToString();

      if (rawKeyName.Length == 1 && char.IsLetter(rawKeyName[0]))
      {
        // JavaScript returns lowercase letters by default unless Shift is explicitly held down
        return isShiftPressed ? rawKeyName.ToUpper() : rawKeyName.ToLower();
      }

      // Return the direct string representation for any unmapped standard keys (e.g., "F1", "CapsLock")
      return rawKeyName;
    }

    [UnsupportedOSPlatform("browser")]
    public static string GetMouseButtonName(MouseButton button)
    {
      return button switch
      {
        MouseButton.Left => "MouseLeft",
        MouseButton.Right => "MouseRight",
        MouseButton.Middle => "MouseMiddle",
        MouseButton.XButton1 => "MouseX1",
        MouseButton.XButton2 => "MouseX2",
        _ => "MouseUnknown"
      };
    }
  }
}