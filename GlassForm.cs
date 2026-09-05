namespace GlassTXT;

/// <summary>
/// 玻璃：无边框、置顶、半透明、可编辑 txt 的窗口。
/// 顶部 32px 隐形拖动区 / Alt+拖动 / 按住边缘空白圈 = 移动；贴边 6px = 缩放；右键 = 小菜单。
/// 无滚动条，滚轮与方向键直接滚动内容。
/// </summary>
internal sealed class GlassForm : Form
{
    private const int WmNcHitTest = 0x0084;
    private const int GripPx = 6;
    private const int DragStripHeight = 32;

    public string FilePath { get; }
    public bool ClickThrough { get; private set; }

    private readonly TextBox _box;
    private readonly Panel _dragStrip;
    private readonly Timer _saveTimer;
    private readonly Timer _reloadTimer;
    private readonly Timer _layoutTimer;
    private readonly FileSystemWatcher _watcher;
    private DateTime _suppressWatchUntil = DateTime.MinValue;
    private Point _boxDragPoint;
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

        _box = new TextBox
        {
            Multiline = true,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            WordWrap = true,
            ScrollBars = ScrollBars.None, // 无滚动条；滚轮、方向键、翻页键照常滚动
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

        Deactivate += (_, _) => FlushSave();
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
        BackColor = glass;
        Opacity = Math.Clamp(a.OpacityPercent, 10, 100) / 100.0;
        _dragStrip.BackColor = glass;
        _box.BackColor = glass;
        _box.ForeColor = ColorUtil.Blend(glass, fontColor, textAlpha);
        _box.Font = new Font(a.FontName, a.FontSize, FontStyle.Regular, GraphicsUnit.Point);
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
        App.Glasses.Remove(this);
    }
}
