using System.IO.Pipes;

namespace GlassTXT;

/// <summary>应用级状态：所有玻璃、托盘、设置窗、配置与单实例 IPC。</summary>
internal static class App
{
    public const string PipeName = "GlassTXT-IPC";

    public static Config Config = null!;
    public static readonly List<GlassForm> Glasses = new();
    public static TrayController Tray = null!;
    public static HotkeyWindow Hotkeys = null!;
    public static SettingsForm? SettingsWindow;
    public static Control Marshal = null!;
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

        var glass = new GlassForm(path);
        Glasses.Add(glass);
        glass.Show();
    }

    public static void ApplyAppearanceToAll()
    {
        foreach (var g in Glasses) g.ApplyAppearance();
    }

    public static void ToggleClickThroughAll()
    {
        bool anyOn = Glasses.Any(g => g.ClickThrough);
        foreach (var g in Glasses) g.SetClickThrough(!anyOn);
    }

    public static void ShowHideAll()
    {
        if (Glasses.Any(g => g.Visible))
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
        var area = Screen.PrimaryScreen!.WorkingArea;
        int i = 0;
        foreach (var g in Glasses)
        {
            g.Bounds = new Rectangle(
                area.Left + Math.Max(0, (area.Width - g.Width) / 2) + i * 36,
                area.Top + Math.Max(0, (area.Height - g.Height) / 2) + i * 28,
                g.Width, g.Height);
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

    public static void Shutdown()
    {
        foreach (var g in Glasses.ToArray())
        {
            g.FlushSave();
            g.SaveLayoutNow();
        }
        SaveConfig();
        Application.Exit();
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
                            Marshal.BeginInvoke(() => OpenGlass(file));
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
