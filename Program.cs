using System.IO.Pipes;
using WinForms = System.Windows.Forms;

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
        "GlassTXT 1.3 使用说明\r\n" +
        "----------------------------------------\r\n" +
        "直接输入，停止输入约半秒自动保存；关闭时也会保存。\r\n" +
        "按住顶部移动玻璃；贴近边缘拖动调整大小。\r\n" +
        "滚轮翻动内容；Ctrl+滚轮缩放文字。\r\n" +
        "中键默认按住并上下移动来滚动，松开停止。\r\n" +
        "文字区域右键：剪切、复制、粘贴、全选，以及设置等操作。\r\n" +
        "托盘右键 → 设置：调整颜色、字体、热键与滚动方式。\r\n" +
        "玻璃和文字不透明度独立，支持 0%-100%，可拖滑块或输入数字。\r\n" +
        "设为 0% 后看不见时，从托盘设置恢复不透明度。\r\n" +
        "Ctrl+Alt+G 开关鼠标穿透，穿透后用同一热键恢复。\r\n" +
        "把其他 txt 拖到玻璃上可打开新玻璃。\r\n" +
        "关闭最后一块玻璃会退出程序；托盘也可退出全部玻璃。\r\n" +
        "\r\n" +
        "本文件仅首次运行时创建，已有内容不会被示例覆盖。\r\n" +
        "可以删除这些说明，保留你自己的待办。\r\n";

    [STAThread]
    private static void Main(string[] args)
    {
        WinForms.Application.SetHighDpiMode(WinForms.HighDpiMode.PerMonitorV2);
        WinForms.Application.EnableVisualStyles();
        WinForms.Application.SetCompatibleTextRenderingDefault(false);
        WinForms.Application.SetUnhandledExceptionMode(WinForms.UnhandledExceptionMode.CatchException);
        WinForms.Application.ThreadException += (_, e) => LogError(e.Exception);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException +=
            (_, e) => LogError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) LogError(ex);
        };

        string file = ResolveFile(args);
        if (TryForwardToRunningInstance(file)) return;

        App.Config = Config.Load();

        using var host = new AppHost(file);
        WinForms.Application.Run(host);
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
