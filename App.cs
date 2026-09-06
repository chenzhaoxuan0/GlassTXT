using System.IO.Pipes;
using System.Windows.Forms.Integration;
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
    public static TaskbarOverlay? Taskbar;
    public static Control Marshal = null!;
    public static string LastHotkeyStatus = "empty";

    public static void OpenGlass(string path)
    {
        if (_shuttingDown) return;
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
        // Keep the WinForms loop; forward keyboard/IME input to this modeless WPF window.
        ElementHost.EnableModelessKeyboardInterop(glass);
        Glasses.Add(glass);
        glass.Show();
    }

    public static void ApplyAppearanceToAll()
    {
        foreach (var g in Glasses) g.ApplyAppearance();
        NotifyTaskbarContentChanged(); // 任务栏文字跟随玻璃字体
    }

    /// <summary>按配置创建/更新/隐藏任务栏浮层（启动与设置页改动时调用）。</summary>
    public static void ApplyTaskbarSettings()
    {
        if (Config.Taskbar.Enabled && Glasses.Count > 0)
        {
            Taskbar ??= new TaskbarOverlay();
            Taskbar.ApplyConfig();
        }
        else
        {
            Taskbar?.HideOverlay();
        }
    }

    /// <summary>玻璃内容变化后让任务栏浮层跟随刷新（内部会合并短时间内的多次请求）。</summary>
    public static void NotifyTaskbarContentChanged() => Taskbar?.ScheduleRefresh();

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
        int i = 0;
        foreach (var g in Glasses)
        {
            g.CenterOnScreen(i * 28);
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
    private static readonly CancellationTokenSource PipeCancellation = new();

    public static void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        PipeCancellation.Cancel();
        foreach (var g in Glasses.ToArray())
            g.Close();
        SettingsWindow?.Close();
        Taskbar?.Dispose();
        Taskbar = null;
        SaveConfig();
        WinForms.Application.ExitThread();
    }

    /// <summary>后台循环：接收后续实例转发来的 "open&lt;TAB&gt;路径"，转交 UI 线程开新玻璃。</summary>
    public static void StartPipeServer(string pipeName = PipeName)
    {
        Task.Run(async () =>
        {
            var token = PipeCancellation.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = CreatePipeServer(pipeName);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    string? line;
                    using (var reader = new StreamReader(server))
                        line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                    if (line is not null && line.StartsWith("open\t", StringComparison.Ordinal))
                    {
                        string file = line["open\t".Length..].Trim();
                        if (file.Length > 0 && !token.IsCancellationRequested)
                            Marshal.BeginInvoke(() => OpenGlass(file));
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    try { await Task.Delay(800, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        });
    }

    private static NamedPipeServerStream CreatePipeServer(string pipeName)
        => new(pipeName, PipeDirection.In,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
}
