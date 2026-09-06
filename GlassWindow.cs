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
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace GlassTXT;

/// <summary>
/// 玻璃：WPF 无边框透明窗口。背景画刷自带玻璃不透明度，文本渲染在其上、
/// 只受文字不透明度影响 —— 两者彻底独立（这是迁移到 WPF 的原因）。
/// 顶部拖动区 / Alt+拖动移动；贴边 6px 缩放（WM_NCHITTEST 自实现）；
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
    private const int ResizeGripPx = 6;            // 贴边缩放判定宽度（DIP）

    public string FilePath { get; }
    public bool ClickThrough { get; private set; }

    /// <summary>当前文本快照（任务栏浮层读取用）。</summary>
    public string TextSnapshot => _box.Text;

    private readonly TextBox _box;
    private readonly Grid _strip;
    private readonly Grid _root;
    private readonly WPFTimer _saveTimer;
    private readonly WPFTimer _reloadTimer;
    private readonly WPFTimer _layoutTimer;
    private readonly WPFTimer _autoScrollTimer;
    private readonly FileSystemWatcher _watcher;
    private Cursor? _cursorBeforeAutoScroll;
    private Point _autoScrollAnchor;
    private Point _autoScrollOffset;
    private bool _autoScrolling;
    private double _autoScrollCarry;
    private int _wheelAccum;
    private bool _wheelWasZoom;
    private bool _loading;
    private bool _dirty;
    private bool _closed;
    private bool _layoutReady;
    private bool _saveWarned;
    private DateTime _suppressWatchUntil = DateTime.MinValue;
    private HwndSource? _source;
    private ZoomBadge? _zoomBadge;

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
        Opacity = 1;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        MinWidth = 160;
        MinHeight = 100;
        Width = 360;
        Height = 420;
        UseLayoutRounding = true;

        _saveTimer = new WPFTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => FlushSave();
        _reloadTimer = new WPFTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _reloadTimer.Tick += (_, _) => ReloadIfExternalChange();
        _layoutTimer = new WPFTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _layoutTimer.Tick += (_, _) => { _layoutTimer.Stop(); SaveLayoutNow(); };
        _autoScrollTimer = new WPFTimer { Interval = TimeSpan.FromMilliseconds(AutoScrollTickMs) };
        _autoScrollTimer.Tick += (_, _) => AutoScrollTick();

        _box = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(12, 0, 12, 12),
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            SelectionBrush = new SolidColorBrush(Color.FromArgb(120, (byte)90, (byte)150, (byte)250)),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, // 无滚动条，滚轮照常滚动
            TextWrapping = TextWrapping.Wrap,
            AllowDrop = true,
            FontFamily = new FontFamily("微软雅黑"),
        };
        _box.TextChanged += (_, _) =>
        {
            App.NotifyTaskbarContentChanged(); // 外部改写（_loading 中）也要同步任务栏
            if (_loading) return;
            _dirty = true;
            _saveTimer.Stop();
            _saveTimer.Start();
        };
        _box.PreviewKeyDown += (_, e) =>
        {
            if (_autoScrolling && e.Key == Key.Escape)
            {
                StopAutoScroll();
                e.Handled = true;
            }
        };
        _box.PreviewMouseDown += BoxMouseDown;
        _box.PreviewMouseMove += BoxMouseMove;
        _box.PreviewMouseUp += BoxMouseUp;
        _box.LostMouseCapture += (_, _) => StopAutoScroll();
        PreviewMouseWheel += BoxPreviewMouseWheel;

        // 顶部隐形拖动区：与玻璃同色，外观无任何变化，专门用于移动窗口
        _strip = new Grid
        {
            Height = DragStripHeight,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
        };
        _strip.MouseDown += StripMouseDown;

        _root = new Grid();
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DragStripHeight) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_strip, 0);
        Grid.SetRow(_box, 1);
        _root.Children.Add(_strip);
        _root.Children.Add(_box);
        _root.MouseDown += (sender, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, _root)) StripMouseDown(sender, e);
        };
        Content = _root;

        var menu = new ContextMenu();
        var miCut = new MenuItem { Header = "剪切" };
        miCut.Click += (_, _) => _box.Cut();
        var miCopy = new MenuItem { Header = "复制" };
        miCopy.Click += (_, _) => _box.Copy();
        var miPaste = new MenuItem { Header = "粘贴" };
        miPaste.Click += (_, _) => _box.Paste();
        var miSelectAll = new MenuItem { Header = "全选" };
        miSelectAll.Click += (_, _) => _box.SelectAll();
        menu.Items.Add(miCut);
        menu.Items.Add(miCopy);
        menu.Items.Add(miPaste);
        menu.Items.Add(miSelectAll);
        menu.Items.Add(new Separator());
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
        menu.Opened += (_, _) =>
        {
            bool hasSelection = _box.SelectionLength > 0;
            miCut.IsEnabled = hasSelection && !_box.IsReadOnly;
            miCopy.IsEnabled = hasSelection;
            miPaste.IsEnabled = !_box.IsReadOnly && ClipboardContainsText();
        };
        ContextMenu = menu;
        _box.ContextMenu = menu;
        _strip.ContextMenu = menu;

        AllowDrop = true;
        PreviewDrop += DropFiles;
        PreviewDragEnter += DragEnterFiles;
        PreviewDragOver += DragEnterFiles;

        ApplyBehavior();
        ApplyAppearance();

        _loading = true;
        _box.Text = TextFile.Read(filePath);
        _loading = false;

        _watcher = new FileSystemWatcher(Path.GetDirectoryName(FilePath)!, Path.GetFileName(FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        _watcher.Changed += (_, _) => OnWatcherEvent();
        _watcher.Renamed += (_, _) => OnWatcherEvent();
        _watcher.Deleted += (_, _) => OnWatcherEvent();
        _watcher.Error += (_, _) => OnWatcherEvent();
        _watcher.EnableRaisingEvents = true;

        Deactivated += (_, _) => { FlushSave(); StopAutoScrollIfActive(); };
        LocationChanged += (_, _) => _layoutTimer.Start();
        SizeChanged += (_, _) => _layoutTimer.Start();
        Loaded += (_, _) =>
        {
            RestoreOrCenterLayout();
            EnsureOnScreen();
            _layoutReady = true;
            KeepTopmostOrder();
        };
        Activated += (_, _) => _box.Focus();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                StopAutoScroll();
                _zoomBadge?.Hide();
                FlushSave();
            }
        };
    }

    private static bool ClipboardContainsText()
    {
        try { return System.Windows.Clipboard.ContainsText(); }
        catch { return false; }
    }

    // ---- 外观 / 行为 ----

    public void ApplyAppearance()
    {
        var a = App.Config.Appearance;
        Color glass = ColorUtil.ParseWpf(a.GlassColor, Color.FromRgb(16, 20, 24));
        Color fontColor = ColorUtil.ParseWpf(a.FontColor, Color.FromRgb(242, 242, 242));
        // Independent brush alpha, never window opacity or a pre-blended text color.
        double textAlpha = Math.Clamp(a.TextOpacityPercent, 0, 100) / 100.0;
        int glassAlpha = Math.Clamp(a.OpacityPercent, 0, 100) * 255 / 100;
        int zoom = Math.Clamp(a.ZoomPercent, 50, 300);
        // v1.1 stores font sizes in points; WPF uses 1/96-inch DIPs.
        double fontSize = Math.Max(6, a.FontSize * zoom / 100.0) * 96 / 72;

        _root.Background = new SolidColorBrush(Color.FromArgb((byte)glassAlpha, glass.R, glass.G, glass.B));
        var textBrush = new SolidColorBrush(fontColor) { Opacity = textAlpha };
        _box.Foreground = textBrush;
        _box.CaretBrush = textBrush;
        _box.FontFamily = new FontFamily(a.FontName);
        _box.FontSize = fontSize;
    }

    public void ApplyBehavior()
    {
        if (MiddleScrollMode != App.Config.Behavior.MiddleScrollMode) StopAutoScroll();
        MiddleScrollMode = App.Config.Behavior.MiddleScrollMode;
        WheelLinesPerNotch = App.Config.Behavior.WheelLinesPerNotch;
        if (MiddleScrollMode == "off") StopAutoScroll();
    }

    // ---- 穿透 ----

    public void SetClickThrough(bool on)
    {
        if (on) StopAutoScroll();
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
        if (!_layoutReady || WindowState == WindowState.Minimized) return;
        var bounds = GetScreenBounds();
        if (bounds.IsEmpty) return;
        App.Config.SetLayout(FilePath, new WindowLayout
        {
            X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height,
        });
        App.SaveConfig();
    }

    private void RestoreOrCenterLayout()
    {
        var layout = App.Config.FindLayout(FilePath);
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (layout is not null && layout.Width >= MinWidth && layout.Height >= MinHeight)
        {
            SetScreenBounds(new Rectangle(layout.X, layout.Y, layout.Width, layout.Height));
        }
        else
        {
            CenterOnScreen();
        }
    }

    public void CenterOnScreen(int offset = 0)
    {
        var area = WinForms.Screen.PrimaryScreen!.WorkingArea;
        var bounds = GetScreenBounds();
        int width = Math.Min(bounds.Width, area.Width);
        int height = Math.Min(bounds.Height, area.Height);
        int x = area.Left + Math.Min((area.Width - width) / 2 + offset, area.Width - width);
        int y = area.Top + Math.Min((area.Height - height) / 2 + offset, area.Height - height);
        SetScreenBounds(new Rectangle(x, y, width, height));
    }

    private void EnsureOnScreen()
    {
        var bounds = GetScreenBounds();
        if (!WinForms.Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
            CenterOnScreen();
    }

    private Rectangle GetScreenBounds()
    {
        if (_source is null || !NativeMethods.GetWindowRect(_source.Handle, out var rect))
            return Rectangle.Empty;
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private void SetScreenBounds(Rectangle bounds)
    {
        if (_source is null) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        // Persist physical pixels, matching the v1.1 WinForms config format.
        NativeMethods.SetWindowPos(_source.Handle, IntPtr.Zero, bounds.X, bounds.Y,
            Math.Max(bounds.Width, (int)Math.Ceiling(MinWidth * dpi.DpiScaleX)),
            Math.Max(bounds.Height, (int)Math.Ceiling(MinHeight * dpi.DpiScaleY)),
            0x0004 | 0x0010); // SWP_NOZORDER | SWP_NOACTIVATE
    }

    // ---- 中键滚动 ----

    private void StripMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && !App.Config.Behavior.LockPosition)
        {
            try { DragMove(); SaveLayoutNow(); } catch { }
        }
    }

    private void BoxMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (MiddleScrollMode == "off") return;
        if (_autoScrolling)
        {
            // toggle：任意点击退出；hold：松键即停到不了这里，仅为防御
            StopAutoScroll();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Middle)
        {
            _box.Focus();
            StartAutoScroll(e.GetPosition(_box));
            e.Handled = true;
        }
    }

    private void BoxMouseMove(object sender, MouseEventArgs e)
    {
        if (_autoScrolling)
        {
            _autoScrollOffset = e.GetPosition(_box);
            e.Handled = true;
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
        if (_autoScrolling && e.ChangedButton == MouseButton.Middle)
        {
            if (MiddleScrollMode == "hold") StopAutoScroll();
            e.Handled = true;
        }
    }

    private void StartAutoScroll(Point anchor)
    {
        if (!_box.CaptureMouse()) return;
        _autoScrolling = true;
        _autoScrollAnchor = anchor;
        _autoScrollOffset = anchor;
        _autoScrollCarry = 0;
        _cursorBeforeAutoScroll = _box.Cursor;
        _box.Cursor = Cursors.SizeNS;
        _autoScrollTimer.Start();
    }

    private void AutoScrollTick()
    {
        if (!_autoScrolling) return;
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
        _box.Cursor = _cursorBeforeAutoScroll;
        if (_box.IsMouseCaptured) _box.ReleaseMouseCapture();
    }

    /// <summary>供玻璃在失焦/隐藏时收尾。</summary>
    public void StopAutoScrollIfActive() => StopAutoScroll();

    // ---- 滚轮 / 缩放 ----

    private void BoxPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        HandleMouseWheel(e.Delta, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }

    private void HandleMouseWheel(int delta, bool zooming)
    {
        if (_wheelWasZoom != zooming) _wheelAccum = 0;
        _wheelWasZoom = zooming;
        _wheelAccum += delta;
        int notches = _wheelAccum / 120;
        if (notches == 0) return;
        _wheelAccum -= notches * 120;
        notches = Math.Clamp(notches, -2, 2);
        if (zooming)
        {
            int before = App.Config.Appearance.ZoomPercent;
            App.AdjustZoom(notches * 10);
            if (App.Config.Appearance.ZoomPercent != before) ShowZoomBadge();
            return;
        }
        int per = Math.Clamp(WheelLinesPerNotch, 1, 10);
        ScrollByLines(-notches * per);
    }

    private void ShowZoomBadge()
    {
        _zoomBadge ??= new ZoomBadge();
        int zoom = Math.Clamp(App.Config.Appearance.ZoomPercent, 50, 300);
        var topCenter = PointToScreen(new Point(ActualWidth / 2, 56));
        _zoomBadge.ShowAt((int)topCenter.X, (int)topCenter.Y, $"缩放 {zoom}%");
    }

    // ---- 保存 / 外部修改 ----

    public void FlushSave()
    {
        _saveTimer.Stop();
        if (!_dirty) return;
        try
        {
            _suppressWatchUntil = DateTime.UtcNow.AddMilliseconds(900);
            TextFile.Write(FilePath, _box.Text);
            _dirty = false;
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
        if (_closed) return;
        if (DateTime.UtcNow < _suppressWatchUntil) return;
        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    private void OnWatcherEvent()
    {
        if (_box.Dispatcher.HasShutdownStarted) return;
        _ = _box.Dispatcher.BeginInvoke(() => ScheduleReloadCheck());
    }

    private void ReloadIfExternalChange()
    {
        _reloadTimer.Stop();
        if (_closed || _dirty || DateTime.UtcNow < _suppressWatchUntil) return;
        try
        {
            string disk = TextFile.Read(FilePath);
            if (disk == _box.Text) return;
            int caret = _box.CaretIndex;
            _loading = true;
            _box.Text = disk;
            _box.CaretIndex = Math.Min(caret, disk.Length);
        }
        catch
        {
            // 文件正被占用或已被删除，等下次事件
        }
        finally { _loading = false; }
    }

    // ---- 拖拽打开新 txt ----

    private void DragEnterFiles(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(WinForms.DataFormats.FileDrop) == true
            && e.Data.GetData(WinForms.DataFormats.FileDrop) is string[] { Length: > 0 } files
            && files[0].EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void DropFiles(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(WinForms.DataFormats.FileDrop) == true
            && e.Data.GetData(WinForms.DataFormats.FileDrop) is string[] { Length: > 0 } files
            && files[0].EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            e.Handled = true;
            App.OpenGlass(files[0]);
        }
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
        const int WM_NCHITTEST = 0x0084;

        if (msg == WM_NCHITTEST && !handled)
        {
            if (App.Config.Behavior.LockPosition || ClickThrough) return IntPtr.Zero;
            long lp = lParam.ToInt64();
            var local = PointFromScreen(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            double wx = local.X, wy = local.Y;
            const int g = ResizeGripPx;
            bool l = wx <= g, r = wx >= ActualWidth - g, t = wy <= g, b = wy >= ActualHeight - g;
            int ht = 0;
            if (t && l) ht = 13;          // HTTOPLEFT
            else if (t && r) ht = 14;     // HTTOPRIGHT
            else if (b && l) ht = 16;     // HTBOTTOMLEFT
            else if (b && r) ht = 17;     // HTBOTTOMRIGHT
            else if (t) ht = 12;          // HTTOP
            else if (b) ht = 15;          // HTBOTTOM
            else if (l) ht = 10;          // HTLEFT
            else if (r) ht = 11;          // HTRIGHT
            if (ht != 0)
            {
                handled = true;
                return new IntPtr(ht);
            }
        }

        return IntPtr.Zero;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        FlushSave();
        SaveLayoutNow();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        StopAutoScroll();
        base.OnClosed(e);
        _watcher.Dispose();
        _saveTimer.Stop();
        _reloadTimer.Stop();
        _layoutTimer.Stop();
        _autoScrollTimer.Stop();
        _zoomBadge?.Dispose();
        _source?.RemoveHook(WndProcHook);
        App.Glasses.Remove(this);
        App.NotifyTaskbarContentChanged(); // 来源玻璃关闭后落到剩余第一块
        if (App.Glasses.Count == 0)
            App.Shutdown(); // 最后一块玻璃关闭 = 整体退出，托盘图标一并移除
    }
}
