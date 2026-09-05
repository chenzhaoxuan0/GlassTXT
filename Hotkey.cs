namespace GlassTXT;

/// <summary>穿透热键的文本表示（如 "Ctrl+Alt+G"）与 RegisterHotKey 参数之间的转换。</summary>
internal static class Hotkey
{
    public static bool TryParse(string? text, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        uint m = 0;
        Keys key = Keys.None;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (i == parts.Length - 1)
            {
                if (!TryParseKey(parts[i], out key)) return false;
            }
            else
            {
                switch (parts[i].ToLowerInvariant())
                {
                    case "ctrl" or "control": m |= NativeMethods.MOD_CONTROL; break;
                    case "alt": m |= NativeMethods.MOD_ALT; break;
                    case "shift": m |= NativeMethods.MOD_SHIFT; break;
                    case "win" or "windows": m |= NativeMethods.MOD_WIN; break;
                    default: return false;
                }
            }
        }

        if (m == 0) return false;
        if (key is Keys.None or Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return false;
        mods = m;
        vk = (uint)((int)key & (int)Keys.KeyCode);
        return true;
    }

    private static bool TryParseKey(string token, out Keys key)
    {
        key = Keys.None;
        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z') { key = Keys.A + (c - 'A'); return true; }
            if (c is >= '0' and <= '9') { key = Keys.D0 + (c - '0'); return true; }
            return false;
        }
        if (token.Length is 2 or 3
            && (token[0] is 'F' or 'f')
            && int.TryParse(token[1..], out int fn) && fn is >= 1 and <= 24)
        {
            key = Keys.F1 + (fn - 1);
            return true;
        }
        return Enum.TryParse(token, ignoreCase: true, out key);
    }
}
