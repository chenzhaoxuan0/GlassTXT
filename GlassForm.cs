namespace GlassTXT;

/// <summary>
/// 玻璃：无边框、置顶、半透明、可编辑 txt 的窗口。
/// 顶部 32px 隐形拖动区 / Alt+拖动 / 按住边缘空白圈 = 移动；贴边 6px = 缩放；右键 = 小菜单。
/// 无滚动条，滚轮与方向键直接滚动内容。
/// </summary>
internal sealed class GlassForm : Form
{
    private const int WmNcHitTest = 0x0084;
    private const int WmMouseWheel = 0x020A;
    private const int GripPx = 6;
    private const int DragStripHeight = 32;

    public string FilePath { get; }
    public bool ClickThrough { get; private set; }

    private readonly GlassTextBox _box;
    private readonly Panel _dragStrip;
    private readonly Timer _saveTimer;
    private readonly Timer _reloadTimer;
    private readonly Timer _layoutTimer;
    private readonly FileSystemWatcher _watcher;
    private DateTime _suppressWatchUntil = DateTime.MinValue;
    private Point _boxDragPoint;
    private ZoomBadge? _zoomBadge;
    private bool _loading;
    private bool _saveWarned;

    public GlassForm(string filePath)
    {
        FilePath = filePath;

        Text = Path.GetFileName(filePath);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        MinimizeBox = MaximizeBox = false;
        MinimumSize = new Size(160, 100);
        Size = new Size(360, 420);
        Padding = new Padding(12);
        DoubleBuffered = true;
        AllowDrop = true;

        _box = new GlassTextBox
        {
            Multiline = true,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            WordWrap = true,
            ScrollBars = ScrollBars.None, // 无滚动条；滚轮由 GlassTextBox 接管
            AcceptsTab = true,
            HideSelection = false,
            AllowDrop = true,
        };
        Controls.Add(_box);

        // 顶部隐形拖动区：与玻璃同色，外观无任何变化，只多了一块好抓的移动区域
        _dragStrip = new Panel
        {
            Dock = DockStyle.Top,
            Height = DragStripHeight,
            BackColor = Color.Black, // ApplyAppearance 会同步成玻璃色
            Cursor = Cursors.SizeAll,
        };
        _dragStrip.MouseDown += StripMouseDown;
        Controls.Add(_dragStrip);

        ApplyAppearance();
        RestoreOrCenterLayout();

        _loading = true;
        _box.Text = TextFile.Read(filePath);
        _loading = false;

        _saveTimer = new Timer { Interval = 500, Enabled = false };
        _saveTimer.Tick += (_, _) => FlushSave();

        _reloadTimer = new Timer { Interval = 400, Enabled = false };
        _reloadTimer.Tick += (_, _) => ReloadIfExternalChange();

        _layoutTimer = new Timer { Interval = 600, Enabled = false };
        _layoutTimer.Tick += (_, _) => { _layoutTimer.Stop(); SaveLayoutNow(); };

        _box.TextChanged += (_, _) =>
        {
            if (_loading) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        };

        // Ctrl+滚轮：调整全局缩放，并在玻璃上方显示倍率徽标
        _box.ZoomRequested += notches =>
        {
            int before = App.Config.Appearance.ZoomPercent;
            App.AdjustZoom(notches * 10);
            if (App.Config.Appearance.ZoomPercent != before) ShowZoomBadge();
        };
        ApplyBehavior();

        MouseDown += FormMouseDown;
        _box.MouseDown += (_, e) => _boxDragPoint = e.Location;
        _box.MouseMove += BoxMouseMove;

        var menu = new ContextMenuStrip();
        menu.Items.Add("设置…", null, (_, _) => App.ShowSettings());
        menu.Items.Add("回正位置", null, (_, _) => { CenterOnScreen(); SaveLayoutNow(); });
        menu.Items.Add("隐藏", null, (_, _) => Hide());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Close());
        ContextMenuStrip = menu;
        _box.ContextMenuStrip = menu;
        _dragStrip.ContextMenuStrip = menu;

        DragEnter += DragEnterFiles;
        DragDrop += DropFiles;
        _box.DragEnter += DragEnterFiles;
        _box.DragDrop += DropFiles;

        _watcher = new FileSystemWatcher(Path.GetDirectoryName(FilePath)!, Path.GetFileName(FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            SynchronizingObject = _box,
        };
        _watcher.Changed += (_, _) => ScheduleReloadCheck();
        _watcher.Renamed += (_, _) => ScheduleReloadCheck();
        _watcher.Deleted += (_, _) => ScheduleReloadCheck();
        _watcher.Error += (_, _) => ScheduleReloadCheck();
        _watcher.EnableRaisingEvents = true;

        Deactivate += (_, _) => { FlushSave(); _box.StopAutoScrollIfActive(); };
        LocationChanged += (_, _) => _layoutTimer.Start();
        Resize += (_, _) => _layoutTimer.Start();
        Shown += (_, _) => EnsureOnScreen();
    }

    // ---- 外观 ----

    public void ApplyAppearance()
    {
        var a = App.Config.Appearance;
        Color glass = ColorUtil.Parse(a.GlassColor, Color.FromArgb(16, 20, 24));
        Color fontColor = ColorUtil.Parse(a.FontColor, Color.FromArgb(242, 242, 242));
        // 文字不透明度独立于玻璃：把文字颜色向玻璃色按比例混合，
        // 视觉上等价于文字以单独的透明度叠在玻璃上，且不影响整窗的玻璃透明度
        double textAlpha = Math.Clamp(a.TextOpacityPercent, 10, 100) / 100.0;
        // 缩放：以设置里的字号为 100% 基准的全局放大/缩小
        int zoom = Math.Clamp(a.ZoomPercent, 50, 300);
        float fontSize = (float)Math.Max(6, a.FontSize * zoom / 100.0);
        BackColor = glass;
        Opacity = Math.Clamp(a.OpacityPercent, 10, 100) / 100.0;
        _dragStrip.BackColor = glass;
        _box.BackColor = glass;
        _box.ForeColor = ColorUtil.Blend(glass, fontColor, textAlpha);
        _box.Font = new Font(a.FontName, fontSize, FontStyle.Regular, GraphicsUnit.Point);
    }

    private void ShowZoomBadge()
    {
        _zoomBadge ??= new ZoomBadge();
        int zoom = Math.Clamp(App.Config.Appearance.ZoomPercent, 50, 300);
        var topCenter = PointToScreen(new Point(Width / 2, 52));
        _zoomBadge.ShowAt(topCenter, $"缩放 {zoom}%");
    }

    /// <summary>把行为设置（中键滚动模式、滚轮行数）同步到文本框。</summary>
    public void ApplyBehavior()
    {
        _box.MiddleScrollMode = App.Config.Behavior.MiddleScrollMode;
        _box.WheelLinesPerNotch = App.Config.Behavior.WheelLinesPerNotch;
        if (App.Config.Behavior.MiddleScrollMode == "off") _box.StopAutoScrollIfActive();
    }

    // ---- 穿透 ----

    public void SetClickThrough(bool on)
    {
        if (ClickThrough == on) return;
        ClickThrough = on;
        if (IsHandleCreated)
            NativeMethods.ModifyExStyle(Handle,
                on ? NativeMethods.WS_EX_TRANSPARENT : 0,
                on ? 0 : NativeMethods.WS_EX_TRANSPARENT);
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        EnsureOnScreen();
        Activate();
    }

    // ---- 位置 ----

    public void SaveLayoutNow()
    {
        App.Config.SetLayout(FilePath, new WindowLayout
        {
            X = Left, Y = Top, Width = Width, Height = Height,
        });
        App.SaveConfig();
    }

    private void RestoreOrCenterLayout()
    {
        var layout = App.Config.FindLayout(FilePath);
        if (layout is not null && layout.Width >= MinimumSize.Width && layout.Height >= MinimumSize.Height)
            SetBounds(layout.X, layout.Y, layout.Width, layout.Height);
        else
            CenterOnScreen();
    }

    private void CenterOnScreen()
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        var size = Size;
        if (size.Width > area.Width) size.Width = area.Width;
        if (size.Height > area.Height) size.Height = area.Height;
        Location = new Point(
            area.Left + (area.Width - size.Width) / 2,
            area.Top + (area.Height - size.Height) / 2);
    }

    private void EnsureOnScreen()
    {
        if (!OnAnyScreen(Bounds)) CenterOnScreen();
    }

    private static bool OnAnyScreen(Rectangle bounds)
        => Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds));

    // ---- 拖动 / 缩放 ----

    private void FormMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && !App.Config.Behavior.LockPosition)
            BeginWindowDrag();
    }

    private void StripMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && !App.Config.Behavior.LockPosition)
            BeginWindowDrag();
    }

    private void BoxMouseMove(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || App.Config.Behavior.LockPosition) return;
        if (!ModifierKeys.HasFlag(Keys.Alt)) return;
        if (Math.Abs(e.X - _boxDragPoint.X) + Math.Abs(e.Y - _boxDragPoint.Y) <= 4) return;
        BeginWindowDrag();
    }

    private void BeginWindowDrag()
    {
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN,
            (IntPtr)NativeMethods.HT_CAPTION, IntPtr.Zero);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseWheel && _box.IsHandleCreated)
        {
            // 焦点或悬停在拖动区/空白圈上时，滚轮也转给文本框
            NativeMethods.SendMessage(_box.Handle, WmMouseWheel, m.WParam, m.LParam);
            return;
        }
        if (m.Msg == WmNcHitTest)
        {
            base.WndProc(ref m);
            if (App.Config.Behavior.LockPosition) return;
            long lp = m.LParam.ToInt64();
            var pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            var size = ClientSize;
            bool left = pt.X < GripPx, right = pt.X >= size.Width - GripPx;
            bool top = pt.Y < GripPx, bottom = pt.Y >= size.Height - GripPx;
            int hit = 0;
            if (top && left) hit = 13;         // HTTOPLEFT
            else if (top && right) hit = 14;   // HTTOPRIGHT
            else if (bottom && left) hit = 16; // HTBOTTOMLEFT
            else if (bottom && right) hit = 17;// HTBOTTOMRIGHT
            else if (top) hit = 12;            // HTTOP
            else if (bottom) hit = 15;         // HTBOTTOM
            else if (left) hit = 10;           // HTLEFT
            else if (right) hit = 11;          // HTRIGHT
            if (hit != 0) m.Result = (IntPtr)hit;
            return;
        }
        base.WndProc(ref m);
    }

    // ---- 保存 / 外部修改 ----

    public void FlushSave()
    {
        _saveTimer.Stop();
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
            MessageBox.Show(this,
                $"自动保存失败：{ex.Message}\n\n文件可能被其他程序占用，恢复后再次编辑将自动重试。",
                "GlassTXT", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ScheduleReloadCheck()
    {
        if (DateTime.UtcNow < _suppressWatchUntil) return;
        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    private void ReloadIfExternalChange()
    {
        _reloadTimer.Stop();
        if (DateTime.UtcNow < _suppressWatchUntil) return;
        try
        {
            string disk = TextFile.Read(FilePath);
            if (disk == _box.Text) return;
            int caret = _box.SelectionStart;
            _loading = true;
            _box.Text = disk;
            _loading = false;
            _box.SelectionStart = Math.Min(caret, disk.Length);
        }
        catch
        {
            // 文件正被占用或已被删除，等下次事件
        }
    }

    // ---- 拖拽打开新 txt ----

    private void DragEnterFiles(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files
            && files[0].EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            e.Effect = DragDropEffects.Copy;
        }
    }

    private void DropFiles(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            App.OpenGlass(files[0]);
    }

    // ---- 生命周期 ----

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        FlushSave();
        SaveLayoutNow();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _watcher.Dispose();
        _saveTimer.Dispose();
        _reloadTimer.Dispose();
        _layoutTimer.Dispose();
        _zoomBadge?.Dispose();
        App.Glasses.Remove(this);
        if (App.Glasses.Count == 0)
            App.Shutdown(); // 最后一块玻璃关闭 = 整体退出，托盘图标一并移除，不留残留
    }
}

/// <summary>
/// 无滚动条的文本框：
/// - 滚轮：Win32 EDIT 控件在没有滚动条时会直接忽略滚轮消息，这里自己接手，
///   用 EM_SCROLL(SB_LINEUP/SB_LINEDOWN) 精确按行滚动。
///   注意不能用 EM_LINESCROLL——它在无滚动条的 EDIT 上会无视行数直接滚到内容末尾。
/// - 中键：三种模式（MiddleScrollMode）——
///   hold（默认）= 按住中键拖动滚动，松开即停；
///   toggle = 点一下持续滚动，任意点击/Esc/失焦退出；
///   off = 中键不负责滚动。
/// - Ctrl+滚轮：触发 ZoomRequested 事件，由宿主玻璃调整缩放并显示倍率。
/// </summary>
internal sealed class GlassTextBox : TextBox
{
    private const int WmMouseWheel = 0x020A;
    private const int WmWheelDelta = 120;
    private const int MkControl = 0x0008;
    private const int EmScroll = 0x00B5;
    private const int SbLineUp = 0;
    private const int SbLineDown = 1;
    private const int AutoScrollTickMs = 30;
    private const int AutoScrollDeadZonePx = 16;   // 锚点死区：轻微手抖不滚动
    private const int AutoScrollPxPerLine = 150;   // 每偏离锚点 150px = 每跳 1 行
    private const int AutoScrollMaxLinesPerTick = 4;

    private readonly Timer _autoScrollTimer;
    private Cursor _cursorBeforeAutoScroll = Cursors.IBeam;
    private Point _autoScrollAnchor;
    private Point _autoScrollOffset;
    private bool _autoScrolling;
    private double _autoScrollCarry;
    private int _wheelAccum;

    /// <summary>Ctrl+滚轮触发：参数为滚过的格数（带符号，向上为正）。</summary>
    public event Action<int>? ZoomRequested;

    /// <summary>中键滚动模式：hold | toggle | off。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string MiddleScrollMode { get; set; } = "hold";

    /// <summary>滚轮每滚一格的行数。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int WheelLinesPerNotch { get; set; } = 3;

    public GlassTextBox()
    {
        _autoScrollTimer = new Timer { Interval = AutoScrollTickMs, Enabled = false };
        _autoScrollTimer.Tick += (_, _) => AutoScrollTick();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autoScrollTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseWheel)
        {
            bool ctrl = ((long)m.WParam & MkControl) != 0;
            _wheelAccum += (short)((long)m.WParam >> 16);
            int notches = _wheelAccum / WmWheelDelta;
            if (notches != 0)
            {
                _wheelAccum -= notches * WmWheelDelta;
                notches = Math.Clamp(notches, -2, 2);
                if (ctrl)
                {
                    ZoomRequested?.Invoke(notches);
                }
                else
                {
                    int per = Math.Clamp(WheelLinesPerNotch, 1, 10);
                    uint cmd = notches > 0 ? (uint)SbLineUp : (uint)SbLineDown; // 滚轮向上 = 回卷内容
                    for (int i = 0; i < Math.Abs(notches * per); i++)
                        NativeMethods.SendMessage(Handle, EmScroll, (IntPtr)cmd, IntPtr.Zero);
                }
            }
            return;
        }
        base.WndProc(ref m);
    }

    // ---- 中键滚动（hold=按住滚动 / toggle=点一下持续滚动 / off=关闭）----

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (MiddleScrollMode == "off")
        {
            base.OnMouseDown(e);
            return;
        }
        if (_autoScrolling)
        {
            // toggle：任意点击退出；hold：松键即停到不了这里，仅为防御
            StopAutoScroll();
            return;
        }
        if (e.Button == MouseButtons.Middle)
        {
            StartAutoScroll(e.Location);
            return;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_autoScrolling)
        {
            _autoScrollOffset = e.Location;
            return;
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (MiddleScrollMode == "hold" && _autoScrolling && e.Button == MouseButtons.Middle)
        {
            StopAutoScroll(); // hold：松开即停
            return;
        }
        base.OnMouseUp(e);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        // hold：捕获丢失即停止；toggle：按键抬起后框架释放捕获属预期，由 tick 夺回
        if (MiddleScrollMode == "hold" && _autoScrolling) StopAutoScroll();
        base.OnMouseCaptureChanged(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_autoScrolling && keyData == Keys.Escape)
        {
            StopAutoScroll();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void StartAutoScroll(Point anchor)
    {
        _autoScrolling = true;
        _autoScrollAnchor = anchor;
        _autoScrollOffset = anchor;
        _autoScrollCarry = 0;
        _cursorBeforeAutoScroll = Cursor;
        Cursor = Cursors.SizeNS; // 上下双向箭头光标，避免与"移动窗口"的十字标混淆
        Capture = true;
        _autoScrollTimer.Start();
    }

    private void AutoScrollTick()
    {
        if (!_autoScrolling) return;
        if (MiddleScrollMode == "toggle" && !Capture) Capture = true; // toggle：捕获被框架放掉就夺回
        int dy = _autoScrollOffset.Y - _autoScrollAnchor.Y; // 正 = 向下滚
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
        uint cmd = lines > 0 ? (uint)SbLineDown : (uint)SbLineUp;
        for (int i = 0; i < Math.Abs(lines); i++)
            NativeMethods.SendMessage(Handle, EmScroll, (IntPtr)cmd, IntPtr.Zero);
    }

    private void StopAutoScroll()
    {
        if (!_autoScrolling) return;
        _autoScrolling = false;
        _autoScrollTimer.Stop();
        Cursor = _cursorBeforeAutoScroll;
        Capture = false;
    }

    /// <summary>供玻璃在失焦/隐藏时收尾。</summary>
    public void StopAutoScrollIfActive() => StopAutoScroll();
}
