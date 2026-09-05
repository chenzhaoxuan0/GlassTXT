using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlassTXT;

public sealed class AppearanceSettings
{
    public string GlassColor { get; set; } = "#101418";
    public int OpacityPercent { get; set; } = 78;
    public int TextOpacityPercent { get; set; } = 100;
    public string FontColor { get; set; } = "#F2F2F2";
    public string FontName { get; set; } = "微软雅黑";
    public int FontSize { get; set; } = 14;
}

public sealed class BehaviorSettings
{
    public string Hotkey { get; set; } = "Ctrl+Alt+G";
    public bool LockPosition { get; set; } = false;
    public bool AutoStart { get; set; } = false;
}

public sealed class WindowLayout
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class Config
{
    public AppearanceSettings Appearance { get; set; } = new();
    public BehaviorSettings Behavior { get; set; } = new();
    public Dictionary<string, WindowLayout> Windows { get; set; } = new();

    [JsonIgnore]
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), JsonOpts);
                if (cfg is not null) return cfg;
            }
        }
        catch
        {
            // 配置损坏时回退默认值，绝不因此起不来
        }
        return new Config();
    }

    public void Save()
    {
        try
        {
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // 配置写失败不影响玻璃继续使用
        }
    }

    public WindowLayout? FindLayout(string file)
    {
        foreach (var (key, layout) in Windows)
            if (string.Equals(key, file, StringComparison.OrdinalIgnoreCase))
                return layout;
        return null;
    }

    public void SetLayout(string file, WindowLayout layout)
    {
        string? key = Windows.Keys.FirstOrDefault(k => string.Equals(k, file, StringComparison.OrdinalIgnoreCase));
        Windows[key ?? Path.GetFullPath(file)] = layout;
    }
}

internal static class ColorUtil
{
    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color Parse(string? text, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        try { return ColorTranslator.FromHtml(text.Trim()); }
        catch { return fallback; }
    }

    /// <summary>把前景色向背景色混合 t（0–1）：t=1 纯前景，t=0 完全隐入背景。</summary>
    public static Color Blend(Color background, Color foreground, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return Color.FromArgb(
            (int)Math.Round(background.R + (foreground.R - background.R) * t),
            (int)Math.Round(background.G + (foreground.G - background.G) * t),
            (int)Math.Round(background.B + (foreground.B - background.B) * t));
    }
}

internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GlassTXT";

    public static bool IsSet()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch { return false; }
    }

    public static void Set(bool on)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"", Microsoft.Win32.RegistryValueKind.String);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // 注册表不可写时静默失败，勾选状态以配置文件为准
        }
    }
}
