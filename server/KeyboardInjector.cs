using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace operation_vote
{
    public static class KeyboardInjector
    {
        // ========================================================================
        // WINDOWS NATIVE P/INVOKE STRUCTURES (user32.dll)
        // ========================================================================
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT 
        { 
            public int dx; 
            public int dy; 
            public uint mouseData; 
            public uint dwFlags; 
            public uint time; 
            public UIntPtr dwExtraInfo; 
        }
        
        [StructLayout(LayoutKind.Sequential)]
        struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        static extern ushort MapVirtualKey(uint uCode, uint uMapType);

        const uint INPUT_MOUSE = 0;
        const uint INPUT_KEYBOARD = 1;
        
        const uint KEYEVENTF_KEYUP = 0x0002;
        const uint KEYEVENTF_SCANCODE = 0x0008;

        const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        const uint MOUSEEVENTF_LEFTUP = 0x0004;
        const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

        public static void InjectKey(string keyString, bool isKeyDown)
        {
            if (string.IsNullOrEmpty(keyString)) return;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Intercept mouse actions before routing to standard key codes
                if (keyString.StartsWith("Mouse", StringComparison.OrdinalIgnoreCase))
                {
                    SendWindowsMouse(keyString, isKeyDown);
                }
                else
                {
                    SendWindowsKey(keyString, isKeyDown);
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                SendLinuxKey(keyString, isKeyDown);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                SendMacKey(keyString, isKeyDown);
            }
        }

        private static void SendWindowsKey(string keyString, bool isKeyDown)
        {
            ushort vKey = ConvertStringToVirtualKey(keyString);
            if (vKey == 0) return;

            // Map the Virtual Key to its hardware scan code. 
            // DirectX/Engine loops in games look for scan codes to guarantee low-latency hardware response.
            ushort scanCode = MapVirtualKey(vKey, 0); 

            INPUT[] inputs = new INPUT[1];
            inputs[0] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vKey,
                        wScan = scanCode,
                        // KEYEVENTF_SCANCODE tells the OS this is a direct hardware emulation
                        dwFlags = KEYEVENTF_SCANCODE | (isKeyDown ? 0 : KEYEVENTF_KEYUP),
                        time = 0,
                        dwExtraInfo = UIntPtr.Zero
                    }
                }
            };

            SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        private static void SendWindowsMouse(string buttonString, bool isKeyDown)
        {
            uint mouseFlags = buttonString.ToLower() switch
            {
                "mouseleft" => isKeyDown ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
                "mouseright" => isKeyDown ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
                "mousemiddle" => isKeyDown ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
                _ => 0
            };

            if (mouseFlags == 0) return;

            INPUT[] inputs = new INPUT[1];
            inputs[0] = new INPUT
            {
                type = INPUT_MOUSE,
                u = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = 0,
                        dy = 0,
                        mouseData = 0,
                        dwFlags = mouseFlags,
                        time = 0,
                        dwExtraInfo = UIntPtr.Zero
                    }
                }
            };

            SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        private static ushort ConvertStringToVirtualKey(string key)
        {
            return key.ToLower() switch
            {
                "space" or " " => 0x20,  // VK_SPACE
                "enter" => 0x0D,        // VK_RETURN
                "up" or "arrowup" => 0x26,
                "w" => 0x57,
                _ => (ushort)(key.Length == 1 ? char.ToUpper(key[0]) : 0)
            };
        }

        // ========================================================================
        // MAC & LINUX OPTIMIZED GAMING SUB-SYSTEMS
        // ========================================================================
        
        [DllImport("libX11.so.6", EntryPoint = "XOpenDisplay")]
        private static extern IntPtr XOpenDisplay(string? display_name);
        
        [DllImport("libXtst.so.6", EntryPoint = "XTestFakeKeyEvent")]
        private static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, bool is_press, ulong delay);

        private static void SendLinuxKey(string keyString, bool isKeyDown)
        {
            // Fallback lookup table mapping common keys to X11 hardware keycodes
            uint keyCode = keyString.ToLower() switch
            {
                "space" or " " => 65,  // Hardware Space Keycode
                "enter" => 36,        // Hardware Enter Keycode
                "up" => 111,          // Arrow Up
                "w" => 25,
                _ => 0
            };

            if (keyCode == 0) return;

            try
            {
                // Dynamic P/Invoke linking bypasses xdotool shell process execution latency (0ms latency drop)
                IntPtr display = XOpenDisplay(null);
                if (display != IntPtr.Zero)
                {
                    XTestFakeKeyEvent(display, keyCode, isKeyDown, 0);
                }
            }
            catch
            {
                // Graceful fallback to structural legacy process channels if native system development header links are broken
                string argName = keyString == " " ? "space" : keyString.ToLower();
                string command = isKeyDown ? "keydown" : "keyup";
                Process.Start(new ProcessStartInfo { FileName = "xdotool", Arguments = $"{command} {argName}", CreateNoWindow = true });
            }
        }

        [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
        private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, bool keyDown);

        [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
        private static extern void CGEventPost(int tap, IntPtr cgEvent);

        private static void SendMacKey(string keyString, bool isKeyDown)
        {
            ushort macKeyCode = keyString.ToLower() switch
            {
                "space" or " " => 49,
                "enter" => 76,
                "up" => 126,
                "w" => 13,
                _ => 0xFF
            };

            if (macKeyCode == 0xFF) return;

            try
            {
                // Direct CoreGraphics Framework synthesis cuts away AppleScript dispatch thread blocks completely
                IntPtr cgEvent = CGEventCreateKeyboardEvent(IntPtr.Zero, macKeyCode, isKeyDown);
                if (cgEvent != IntPtr.Zero)
                {
                    CGEventPost(0, cgEvent); // kCGHIDEventTap = 0
                }
            }
            catch
            {
                // Legacy applescript loop fallback
                string escaped = keyString.Replace("\"", "\\\"").Replace(" ", "space");
                Process.Start(new ProcessStartInfo { FileName = "osascript", Arguments = $"-e \"tell application \\\"System Events\\\" to keystroke \\\"{escaped}\\\"\"", CreateNoWindow = true });
            }
        }
    }
}