using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WPFTimer = System.Windows.Threading.DispatcherTimer;
using WinForms = System.Windows.Forms;
using TextBox = System.Windows.Controls.TextBox;
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using TextOptions = System.Windows.Media.TextOptions;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using DragDropEffects = System.Windows.DragDropEffects;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;

namespace GlassTXT;

/// <summary>
/// 玻璃：WPF 无边框透明窗口。背景画刷自带玻璃不透明度，文本渲染在其上、
/// 只受文字不透明度影响 —— 两者彻底独立（这是迁移到 WPF 的原因）。
/// 顶部拖动区 / Alt+拖动移动，WindowChrome 提供贴边缩放；
/// 中键滚动支持 hold（按住拖动，松开即停）/ toggle（点一下持续滚动）/ off 三种模式；
/// 滚轮按设置行数滚动，Ctrl+滚轮缩放并显示倍率。
/// </summary>
internal sealed class GlassWindow : Window
{
    private const int AutoScrollTickMs = 30;
    private const int AutoScrollDeadZonePx = 16;   // 锚点死区：轻微手抖不滚动
    private const int AutoScrollPxPerLine = 150;   // 每偏离锚点 150px = 每跳 1 行
    private const int AutoScrollMaxLinesPerTick = 4;
    private const double DragStripHeight = 32;

    public string FilePath { get; }
    public bool ClickThrough { get; private set; }

    private readonly TextBox _box;
    private readonly Grid _strip;
    private readonly Grid _root;
    private readonly WPFTimer _saveTimer;
    private readonly WPFTimer _reloadTimer;
    private readonly WPFTimer _layoutTimer;
    private readonly WPFTimer _autoScrollTimer;
    private readonly FileSystemWatcher _watcher;
    private Cursor _cursorBeforeAutoScroll = Cursors.IBeam;
    private Point _autoScrollAnchor;
    private Point _autoScrollOffset;
    private bool _autoScrolling;
    private double _autoScrollCarry;
    private int _wheelAccum;
    private bool _loading;
    private bool _saveWarned;
    private DateTime _suppressWatchUntil = DateTime.MinValue;
    private HwndSource? _source;
    private ZoomBadge? _zoomBadge;

    /// <summary>Ctrl+滚轮触发：参数为滚过的格数（带符号，向上为正）。</summary>
    public event Action<int>? ZoomRequested;

    /// <summary>中键滚动模式：hold | toggle | off。</summary>
    public string MiddleScrollMode { get; set; } = "hold";

    /// <summary>滚轮每滚一格的行数。</summary>
    public int WheelLinesPerNotch { get; set; } = 3;

    public GlassWindow(string filePath)
    {
        FilePath = filePath;
        Title = Path.GetFileName(filePath);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        MinWidth = 160;
        MinHeight = 100;
        Width = 360;
        Height = 420;
        UseLayoutRounding = true;


        _box = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            SelectionBrush = new SolidColorBrush(Color.FromArgb(120, (byte)90, (byte)150, (byte)250)),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            TextWrapping = TextWrapping.Wrap,
            AllowDrop = true,
            FontFamily = new FontFamily("微软雅黑"),
        };
        _box.TextChanged += (_, _) =>
        {
            if (_loading) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        };
        _box.PreviewKeyDown += (_, e) =>
        {
            if (_autoScrolling && e.Key == Key.Escape) StopAutoScroll();
        };
        _box.MouseDown += BoxMouseDown;
        _box.MouseMove += BoxMouseMove;
        _box.MouseUp += BoxMouseUp;
        _box.LostMouseCapture += (_, _) => { if (MiddleScrollMode == "hold") StopAutoScroll(); };
        _box.AllowDrop = true;
        _box.Drop += DropFiles;
        _box.PreviewDragEnter += DragEnterFiles;

        _strip = new Grid
        {
            Height = DragStripHeight,
            Background = Brushes.Transparent, // 隐形拖动区，与玻璃同色
            Cursor = Cursors.SizeAll,
        };
        _strip.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && !App.Config.Behavior.LockPosition)
            {
                try { DragMove(); SaveLayoutNow(); } catch { /* 关闭等时机下 DragMove 会抛 */ }
            }
        };

        _root = new Grid();
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DragStripHeight) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_strip, 0);
        Grid.SetRow(_box, 1);
        _root.Children.Add(_strip);
        _root.Children.Add(_box);
        Content = _root;

        var menu = new ContextMenu();
        var miSettings = new MenuItem { Header = "设置…" };
        miSettings.Click += (_, _) => App.ShowSettings();
        var miRecenter = new MenuItem { Header = "回正位置" };
        miRecenter.Click += (_, _) => { CenterOnScreen(); SaveLayoutNow(); };
        var miHide = new MenuItem { Header = "隐藏" };
        miHide.Click += (_, _) => Hide();
        var miExit = new MenuItem { Header = "退出" };
        miExit.Click += (_, _) => Close();
        menu.Items.Add(miSettings);
        menu.Items.Add(miRecenter);
        menu.Items.Add(miHide);
        menu.Items.Add(new Separator());
        menu.Items.Add(miExit);

        _autoScrollTimer = new WPFTimer { Interval = TimeSpan.FromMilliseconds(AutoScrollTickMs) };
        _autoScrollTimer.Tick += (_, _) => AutoScrollTick();

        AllowDrop = true;
        Drop += DropFiles;
        PreviewDragEnter += DragEnterFiles;

        ApplyBehavior();
        ApplyAppearance();
        RestoreOrCenterLayout();

        _loading = true;
        _box.Text = TextFile.Read(filePath);
        _loading = false;


        Loaded += (_, _) => { EnsureOnScreen(); KeepTopmostOrder(); _box.Focus(); };
    }

    // ---- 外观 / 行为 ----

    public void ApplyAppearance()
    {
        var a = App.Config.Appearance;
        Color glass = ColorUtil.ParseWpf(a.GlassColor, Color.FromRgb(16, 20, 24));
        Color fontColor = ColorUtil.ParseWpf(a.FontColor, Color.FromRgb(242, 242, 242));
        // 文字不透明度：文字颜色向玻璃色混合的比例；玻璃不透明度画在背景画刷上，
        // 文字渲染在背景之上 —— 两者彻底独立
        double textAlpha = Math.Clamp(a.TextOpacityPercent, 10, 100) / 100.0;
        int glassAlpha = Math.Clamp(a.OpacityPercent, 10, 100) * 255 / 100;
        int zoom = Math.Clamp(a.ZoomPercent, 50, 300);
        double fontSize = Math.Max(6, a.FontSize * zoom / 100.0);

        _root.Background = new SolidColorBrush(Color.FromArgb((byte)glassAlpha, glass.R, glass.G, glass.B));
        var textBrush = new SolidColorBrush(ColorUtil.BlendWpf(glass, fontColor, textAlpha));
        _box.Foreground = textBrush;
        _box.CaretBrush = textBrush;
        _box.FontFamily = new FontFamily(a.FontName);
        _box.FontSize = fontSize;
    }

    public void ApplyBehavior()
    {
        MiddleScrollMode = App.Config.Behavior.MiddleScrollMode;
        WheelLinesPerNotch = App.Config.Behavior.WheelLinesPerNotch;
        if (MiddleScrollMode == "off") StopAutoScroll();
    }

    // ---- 穿透 ----

    public void SetClickThrough(bool on)
    {
        ClickThrough = on;
        ApplyClickThrough();
    }

    private void ApplyClickThrough()
    {
        if (_source is null) return;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x20;
        IntPtr hwnd = _source.Handle;
        long style = NativeMethods.GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        long next = ClickThrough ? (style | WS_EX_TRANSPARENT) : (style & ~WS_EX_TRANSPARENT);
        NativeMethods.SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(next));
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        EnsureOnScreen();
        KeepTopmostOrder();
        Activate();
    }

    private void KeepTopmostOrder()
    {
        Topmost = false;
        Topmost = true;
    }

    // ---- 位置 ----

    public void SaveLayoutNow()
    {
        App.Config.SetLayout(FilePath, new WindowLayout
        {
            X = (int)Left, Y = (int)Top, Width = (int)ActualWidth, Height = (int)ActualHeight,
        });
        App.SaveConfig();
    }

    private void RestoreOrCenterLayout()
    {
        var layout = App.Config.FindLayout(FilePath);
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (layout is not null && layout.Width >= MinWidth && layout.Height >= MinHeight)
        {
            Left = layout.X;
            Top = layout.Y;
            Width = layout.Width;
            Height = layout.Height;
        }
        else
        {
            CenterOnScreen();
        }
    }

    private void CenterOnScreen()
    {
        double waW = SystemParameters.WorkArea.Width;
        double waH = SystemParameters.WorkArea.Height;
        Width = Math.Min(Width, waW);
        Height = Math.Min(Height, waH);
        Left = Math.Max(0, (waW - Width) / 2);
        Top = Math.Max(0, (waH - Height) / 2);
    }

    private void EnsureOnScreen()
    {
        if (!OnAnyScreen(new Rect(Left, Top, ActualWidth, ActualHeight))) CenterOnScreen();
    }

    private bool OnAnyScreen(Rect rectDip)
    {
        var scale = GetDpiScale();
        foreach (WinForms.Screen screen in WinForms.Screen.AllScreens)
        {
            var wa = screen.WorkingArea;
            var waDip = new Rect(wa.Left / scale.X, wa.Top / scale.Y, wa.Width / scale.X, wa.Height / scale.Y);
            if (waDip.IntersectsWith(rectDip)) return true;
        }
        return false;
    }

    private Point GetDpiScale()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(dpi.DpiScaleX, dpi.DpiScaleY);
    }

    // ---- 中键滚动 ----

    private void BoxMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (MiddleScrollMode == "off") return;
        if (e.ChangedButton == MouseButton.Left) _box.Focus(); // 点击置焦，保证键盘输入
        if (_autoScrolling)
        {
            // toggle：任意点击退出；hold：松键即停到不了这里，仅为防御
            StopAutoScroll();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Middle)
        {
            StartAutoScroll(e.GetPosition(_box));
            e.Handled = true;
        }
    }

    private void BoxMouseMove(object sender, MouseEventArgs e)
    {
        if (_autoScrolling)
        {
            _autoScrollOffset = e.GetPosition(_box);
            return;
        }
        // Alt+左键拖动 = 移动窗口（顶部拖动区之外的第二种移动方式）
        if (e.LeftButton == MouseButtonState.Pressed
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)
            && !App.Config.Behavior.LockPosition)
        {
            try { DragMove(); } catch { }
        }
    }

    private void BoxMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (MiddleScrollMode == "hold" && _autoScrolling && e.ChangedButton == MouseButton.Middle)
        {
            StopAutoScroll(); // hold：松开即停
            e.Handled = true;
        }
    }

    private void StartAutoScroll(Point anchor)
    {
        _autoScrolling = true;
        _autoScrollAnchor = anchor;
        _autoScrollOffset = anchor;
        _autoScrollCarry = 0;
        _cursorBeforeAutoScroll = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.SizeNS; // 上下双向箭头，避免与"移动窗口"的十字标混淆
        _box.CaptureMouse();
        _autoScrollTimer.Start();
    }

    private void AutoScrollTick()
    {
        if (!_autoScrolling) return;
        if (MiddleScrollMode == "toggle" && !_box.IsMouseCaptured) _box.CaptureMouse();
        int dy = (int)(_autoScrollOffset.Y - _autoScrollAnchor.Y); // 正 = 向下滚
        int effective = Math.Abs(dy) - AutoScrollDeadZonePx;
        if (effective <= 0)
        {
            _autoScrollCarry = 0;
            return;
        }
        double lines = effective / (double)AutoScrollPxPerLine * Math.Sign(dy);
        _autoScrollCarry += lines;
        int whole = Math.Clamp((int)_autoScrollCarry, -AutoScrollMaxLinesPerTick, AutoScrollMaxLinesPerTick);
        if (whole == 0) return;
        _autoScrollCarry -= whole;
        ScrollByLines(whole);
    }

    private void ScrollByLines(int lines)
    {
        for (int i = 0; i < Math.Abs(lines); i++)
        {
            if (lines > 0) _box.LineDown();
            else _box.LineUp();
        }
    }

    private void StopAutoScroll()
    {
        if (!_autoScrolling) return;
        _autoScrolling = false;
        _autoScrollTimer.Stop();
        Mouse.OverrideCursor = _cursorBeforeAutoScroll;
        if (_box.IsMouseCaptured) _box.ReleaseMouseCapture();
    }

    /// <summary>供玻璃在失焦/隐藏时收尾。</summary>
    public void StopAutoScrollIfActive() => StopAutoScroll();

    // ---- 滚轮 / 缩放 ----

    private void BoxPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _wheelAccum += e.Delta;
        }
        else
        {
            _wheelAccum += e.Delta;
        }
        int notches = _wheelAccum / 120;
        if (notches == 0) return;
        _wheelAccum -= notches * 120;
        notches = Math.Clamp(notches, -2, 2);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ZoomRequested?.Invoke(notches);
            return;
        }
        int per = Math.Clamp(WheelLinesPerNotch, 1, 10);
        int lines = notches * per;
        for (int i = 0; i < Math.Abs(lines); i++)
        {
            if (lines > 0) _box.LineDown();
            else _box.LineUp();
        }
    }

    private void ShowZoomBadge()
    {
        _zoomBadge ??= new ZoomBadge();
        int zoom = Math.Clamp(App.Config.Appearance.ZoomPercent, 50, 300);
        var topCenter = PointToScreen(new Point(ActualWidth / 2 - 60, 56));
        _zoomBadge.ShowAt((int)topCenter.X, (int)topCenter.Y, $"缩放 {zoom}%");
    }

    // ---- 保存 / 外部修改 ----

    public void FlushSave()
    {
        try
        {
            _suppressWatchUntil = DateTime.UtcNow.AddMilliseconds(900);
            TextFile.Write(FilePath, _box.Text);
            _saveWarned = false;
        }
        catch when (_saveWarned)
        {
            // 已经提醒过一次，等文件恢复后再次编辑才重试
        }
        catch (Exception ex)
        {
            _saveWarned = true;
            WinForms.MessageBox.Show($"自动保存失败：{ex.Message}\n\n文件可能被其他程序占用，恢复后再次编辑将自动重试。",
                "GlassTXT", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Warning);
        }
    }

    private void ScheduleReloadCheck()
    {
    }

    private void OnWatcherEvent()
    {
        if (DateTime.UtcNow < _suppressWatchUntil) return;
        _box.Dispatcher.BeginInvoke(() => ScheduleReloadCheck());
    }

    private void ReloadIfExternalChange()
    {
        _reloadTimer.Stop();
        if (DateTime.UtcNow < _suppressWatchUntil) return;
        try
        {
            string disk = TextFile.Read(FilePath);
            if (disk == _box.Text) return;
            int caret = _box.CaretIndex;
            _loading = true;
            _box.Text = disk;
            _loading = false;
            _box.CaretIndex = Math.Min(caret, disk.Length);
        }
        catch
        {
            // 文件正被占用或已被删除，等下次事件
        }
    }

    // ---- 拖拽打开新 txt ----

    private void DragEnterFiles(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(WinForms.DataFormats.FileDrop) == true
            && e.Data.GetData(WinForms.DataFormats.FileDrop) is string[] { Length: > 0 } files
            && files[0].EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            e.Effects = DragDropEffects.Copy;
        }
    }

    private void DropFiles(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(WinForms.DataFormats.FileDrop) is string[] { Length: > 0 } files)
            App.OpenGlass(files[0]);
    }

    // ---- 生命周期 / 原生挂钩 ----

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = (HwndSource)HwndSource.FromVisual(this);
        _source?.AddHook(WndProcHook);
        ApplyClickThrough();
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_KEYDOWN = 0x0100;
        const long VK_PACKET = 0xE7;
        if (msg == WM_KEYDOWN && wParam.ToInt64() == VK_PACKET)
        {
            // SendInput 的 Unicode 注入以 VK_PACKET 发 KEYDOWN：WPF 处理它会崩溃，
            // 且键盘布局对 VK_PACKET 无法合成 WM_CHAR —— 从 lParam 扫码字节取出
            // Unicode 字符，手动向文本框引发 TextInput 完成输入
            handled = true;
            char c = (char)((lParam.ToInt64() >> 16) & 0xFF);
            if (!char.IsControl(c) && c != ' ')
            {
                var composition = new TextComposition(InputManager.Current, _box, c.ToString());
                _box.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                {
                    RoutedEvent = TextCompositionManager.TextInputEvent,
                });
            }
        }
        return IntPtr.Zero;
    }

    private void Log(string line)
    {
        try { File.AppendAllText(@"C:\Users\chenziyu\AppData\Local\Temp\glass-kb.log",
            $"[{DateTime.Now:HH:mm:ss.fff}] {line}\r\n"); } catch { }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _autoScrollTimer.Stop();
        App.Glasses.Remove(this);
        if (App.Glasses.Count == 0)
            App.Shutdown(); // 最后一块玻璃关闭 = 整体退出，托盘图标一并移除
    }
}
