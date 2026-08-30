using System.Runtime.InteropServices;
using System.Text;

namespace UltraNovaCtl.Core;

/// <summary>
/// Sends a key combination to whatever window has focus, so a panel button can do things
/// no MIDI message reaches: undo, save, switching tracks, transport in software that has
/// no MIDI learn.
///
/// Keys are sent as virtual key codes rather than scan codes, because the target is an
/// application shortcut - it wants "the Z key" as the layout defines it, not a physical
/// position. Modifiers are pressed around the key and released in reverse order, which is
/// what every keyboard driver does and what applications expect.
/// </summary>
public static class Keystroke
{
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public KEYBDINPUT ki;
        // The union is 32 bytes on x64 while KEYBDINPUT is 24, so pad by 8. Getting the
        // size wrong makes SendInput silently do nothing at all.
        public long padding;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, INPUT[] inputs, int size);

    const uint InputKeyboard = 1;
    const uint KeyUp = 0x0002;
    const uint ExtendedKey = 0x0001;

    // Modifier virtual key codes.
    const ushort VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkWin = 0x5B;

    /// <summary>Keys that live on the extended part of the keyboard.</summary>
    static readonly HashSet<ushort> Extended = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5C, 0x6F, 0x90,
    };

    /// <summary>Names accepted in a gesture, beyond single characters and F1-F24.</summary>
    static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = 0x20, ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09,
        ["esc"] = 0x1B, ["escape"] = 0x1B, ["backspace"] = 0x08, ["back"] = 0x08,
        ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["ins"] = 0x2D,
        ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
        ["plus"] = 0xBB, ["minus"] = 0xBD, ["comma"] = 0xBC, ["period"] = 0xBE,
        ["numpad0"] = 0x60, ["numpad1"] = 0x61, ["numpad2"] = 0x62, ["numpad3"] = 0x63,
        ["numpad4"] = 0x64, ["numpad5"] = 0x65, ["numpad6"] = 0x66, ["numpad7"] = 0x67,
        ["numpad8"] = 0x68, ["numpad9"] = 0x69,
        ["multiply"] = 0x6A, ["add"] = 0x6B, ["subtract"] = 0x6D, ["divide"] = 0x6F,
    };

    /// <summary>
    /// Parse a gesture such as "Ctrl+Shift+Z" or "Alt+F4". Returns false when nothing
    /// usable is in the string, so a bad setting simply does nothing rather than sending
    /// a random key.
    /// </summary>
    public static bool TryParse(string gesture, out ushort key, out List<ushort> modifiers)
    {
        key = 0;
        modifiers = new List<ushort>();
        if (string.IsNullOrWhiteSpace(gesture)) return false;

        foreach (string rawPart in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string part = rawPart.Trim();
            if (part.Length == 0) continue;

            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers.Add(VkControl); continue;
                case "shift": modifiers.Add(VkShift); continue;
                case "alt": modifiers.Add(VkMenu); continue;
                case "win": case "cmd": modifiers.Add(VkWin); continue;
            }

            if (Named.TryGetValue(part, out ushort named)) { key = named; continue; }

            if (part.Length > 1 && (part[0] == 'F' || part[0] == 'f')
                && int.TryParse(part.Substring(1), out int fn) && fn is >= 1 and <= 24)
            {
                key = (ushort)(0x70 + fn - 1);
                continue;
            }

            if (part.Length == 1)
            {
                char c = char.ToUpperInvariant(part[0]);
                if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') key = c;
            }
        }

        return key != 0;
    }

    /// <summary>Press and release the combination. Returns false if nothing was sent.</summary>
    public static bool Send(string gesture)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!TryParse(gesture, out ushort key, out var modifiers)) return false;

        var events = new List<INPUT>();
        foreach (ushort m in modifiers) events.Add(Make(m, false));
        events.Add(Make(key, false));
        events.Add(Make(key, true));
        // Release modifiers in reverse, the way a hand would let go.
        for (int i = modifiers.Count - 1; i >= 0; i--) events.Add(Make(modifiers[i], true));

        var array = events.ToArray();
        return SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>()) == array.Length;
    }

    static INPUT Make(ushort vk, bool up)
    {
        uint flags = up ? KeyUp : 0;
        if (Extended.Contains(vk)) flags |= ExtendedKey;
        return new INPUT
        {
            type = InputKeyboard,
            ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero },
        };
    }

    /// <summary>Tidy a gesture for display: "ctrl+z" becomes "Ctrl+Z".</summary>
    public static string Normalise(string gesture)
    {
        if (!TryParse(gesture, out ushort key, out var modifiers)) return "";
        var sb = new StringBuilder();
        foreach (ushort m in modifiers)
            sb.Append(m switch
            {
                VkControl => "Ctrl+",
                VkShift => "Shift+",
                VkMenu => "Alt+",
                VkWin => "Win+",
                _ => "",
            });

        foreach (var kv in Named)
            if (kv.Value == key)
            {
                sb.Append(char.ToUpperInvariant(kv.Key[0]) + kv.Key.Substring(1));
                return sb.ToString();
            }

        if (key is >= 0x70 and <= 0x87) sb.Append('F').Append(key - 0x70 + 1);
        else sb.Append((char)key);
        return sb.ToString();
    }
}
