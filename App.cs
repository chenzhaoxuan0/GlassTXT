using System.IO.Pipes;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace GlassTXT;

/// <summary>应用级状态：所有玻璃、托盘、设置窗、配置与单实例 IPC。</summary>
internal static class App
{
    public const string PipeName = "GlassTXT-IPC";

    public static Config Config = null!;
    public static readonly List<GlassWindow> Glasses = new();
    public static TrayController Tray = null!;
    public static HotkeyWindow Hotkeys = null!;
    public static SettingsForm? SettingsWindow;
    public static System.Windows.Threading.Dispatcher UiDispatcher = null!;
    public static string LastHotkeyStatus = "empty";

    public static void OpenGlass(string path)
    {
        try { path = Path.GetFullPath(path); } catch { return; }
        if (!File.Exists(path))
        {
            try { File.WriteAllText(path, string.Empty, new UTF8Encoding(false)); }
            catch { return; }
        }

        var existing = Glasses.FirstOrDefault(g =>
            string.Equals(g.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.ShowAndActivate();
            return;
        }

        var glass = new GlassWindow(path);
        Glasses.Add(glass);
        glass.Show();
    }

    public static void ApplyAppearanceToAll()
    {
        foreach (var g in Glasses) g.ApplyAppearance();
    }

    /// <summary>调整全局缩放比例（百分比增量），作用于所有玻璃并立即持久化。</summary>
    public static void AdjustZoom(int deltaPercent)
    {
        int value = Math.Clamp(Config.Appearance.ZoomPercent + deltaPercent, 50, 300);
        if (value == Config.Appearance.ZoomPercent) return;
        Config.Appearance.ZoomPercent = value;
        ApplyAppearanceToAll();
        SaveConfig();
    }

    /// <summary>把行为设置同步到所有玻璃。</summary>
    public static void ApplyBehaviorToAll()
    {
        foreach (var g in Glasses) g.ApplyBehavior();
    }

    public static void ToggleClickThroughAll()
    {
        bool anyOn = Glasses.Any(g => g.ClickThrough);
        foreach (var g in Glasses) g.SetClickThrough(!anyOn);
    }

    public static void ShowHideAll()
    {
        if (Glasses.Any(g => g.IsVisible))
        {
            foreach (var g in Glasses) g.Hide();
        }
        else
        {
            foreach (var g in Glasses) g.ShowAndActivate();
        }
    }

    public static void RecenterAll()
    {
        double waW = SystemParameters.WorkArea.Width;
        double waH = SystemParameters.WorkArea.Height;
        int i = 0;
        foreach (var g in Glasses)
        {
            g.Left = Math.Max(0, (waW - g.ActualWidth) / 2) + i * 36;
            g.Top = Math.Max(0, (waH - g.ActualHeight) / 2) + i * 28;
            i++;
        }
        foreach (var g in Glasses) g.SaveLayoutNow();
    }

    public static void ShowSettings()
    {
        if (SettingsWindow is { IsDisposed: false })
        {
            SettingsWindow.Activate();
            return;
        }
        SettingsWindow = new SettingsForm();
        SettingsWindow.Show();
    }

    public static string ApplyHotkey(string? hotkey)
    {
        LastHotkeyStatus = Hotkeys.Apply(hotkey);
        return LastHotkeyStatus;
    }

    public static void SaveConfig() => Config.Save();

    private static bool _shuttingDown;

    public static void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        foreach (var g in Glasses.ToArray())
        {
            g.FlushSave();
            g.SaveLayoutNow();
            g.Close();
        }
        SaveConfig();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvokeShutdown(
            System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>后台循环：接收后续实例转发来的 "open&lt;TAB&gt;路径"，转交 UI 线程开新玻璃。</summary>
    public static void StartPipeServer()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = CreatePipeServer();
                    await server.WaitForConnectionAsync().ConfigureAwait(false);
                    string? line;
                    using (var reader = new StreamReader(server))
                        line = await reader.ReadLineAsync().ConfigureAwait(false);
                    server = null; // reader 的 Dispose 已释放 server
                    if (line is not null && line.StartsWith("open\t", StringComparison.Ordinal))
                    {
                        string file = line["open\t".Length..].Trim();
                        if (file.Length > 0)
                            UiDispatcher.BeginInvoke(() => OpenGlass(file));
                    }
                }
                catch
                {
                    try { server?.Dispose(); } catch { /* 下轮重试 */ }
                    try { await Task.Delay(800).ConfigureAwait(false); } catch { }
                }
            }
        });
    }

    private static NamedPipeServerStream CreatePipeServer()
        => new(PipeName, PipeDirection.In,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
}
