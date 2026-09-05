using System.IO.Pipes;

namespace GlassTXT;

internal static class Program
{
    private const string DefaultFileName = "todo.txt";

    private const string SampleTodo =
        "今日待办\r\n" +
        "----------------------------------------\r\n" +
        "[ ] 把这份示例改成你自己的每日待办\r\n" +
        "[ ] 按 Ctrl+Alt+G 开关鼠标穿透\r\n" +
        "[ ] 托盘图标右键 → 设置… 可以换颜色、字体、热键\r\n" +
        "\r\n" +
        "小抄：Alt+拖动=移动 ｜ 边缘=拉伸 ｜ 右键=菜单 ｜ 停止输入即自动保存\r\n";

    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogError(e.Exception);

        string file = ResolveFile(args);
        if (TryForwardToRunningInstance(file)) return;

        App.Config = Config.Load();
        App.Marshal = new Control();
        _ = App.Marshal.Handle;
        App.StartPipeServer();
        App.Tray = new TrayController();
        App.OpenGlass(file);
        Application.Run(new AppHost());
    }

    private sealed class AppHost : ApplicationContext
    {
        public AppHost()
        {
            App.Hotkeys = new HotkeyWindow();
            App.ApplyHotkey(App.Config.Behavior.Hotkey);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                App.Hotkeys?.Dispose();
                App.Tray?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>没有参数时打开 exe 旁边的 todo.txt（不存在则创建示例）；参数指定的文件不存在则创建空文件。</summary>
    private static string ResolveFile(string[] args)
    {
        string defaultPath = Path.Combine(AppContext.BaseDirectory, DefaultFileName);
        string path = args.Length > 0 ? args[0] : defaultPath;
        try { path = Path.GetFullPath(path); }
        catch { path = defaultPath; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
            {
                string content = string.Equals(path, defaultPath, StringComparison.OrdinalIgnoreCase)
                    ? SampleTodo
                    : string.Empty;
                File.WriteAllText(path, content, new UTF8Encoding(false));
            }
        }
        catch
        {
            // 打不开的路径交给窗口内提示，不让进程起不来
        }
        return path;
    }

    /// <summary>已有实例在运行时，把文件路径转发给它并退出（同进程开新玻璃，托盘只有一个图标）。</summary>
    private static bool TryForwardToRunningInstance(string file)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", App.PipeName, PipeDirection.Out);
            client.Connect(1200);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine("open\t" + file);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void LogError(Exception ex)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n");
        }
        catch
        {
            // 日志都写不进去就只能放弃
        }
    }
}
