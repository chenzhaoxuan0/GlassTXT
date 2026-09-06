namespace GlassTXT;

/// <summary>
/// 任务栏待办浮层（TranslucentTB 风格）：把玻璃里选定的行以白色文字常驻任务栏。
/// 不改动系统任务栏本身，而是创建 WS_EX_LAYERED 分层窗口后 SetParent 嵌入
/// Shell_TrayWnd，用 32 位 ARGB 逐像素 alpha 画半透明底色与抗锯齿文字，
/// 视觉上如同印在任务栏上；任务栏自动隐藏、全屏或被资源管理器重建时
/// 随父窗口一并消失并自动恢复。
/// 默认停靠位置会枚举任务栏上已有的子窗口（托盘、任务按钮、流量监控等
/// 任务栏工具）做空隙检测，挑最靠近中间的空隙，避免遮住任何已有内容。
/// </summary>
internal sealed class TaskbarOverlay : Form
{
    private const int PadX = 14, PadY = 4;
    private const int MinWidth = 48, MaxWidth = 760;
    private const int TrayGap = 8;          // 与托盘区的间距
    private const int TrayFallback = 200;   // 找不到 TrayNotifyWnd 时给托盘预留的宽度
    private const float LineSpacing = 1.12f; // 行距（相对字体行高的压缩系数）
    private const float MinFontPx = 9f;      // 字号下限（物理像素），保证可读

    private readonly Timer _poll = new() { Interval = 1000 };
    private readonly Timer _refresh = new() { Interval = 120 };
    private string[] _lines = Array.Empty<string>();
    private uint _dpi = 96;
    private NativeMethods.WindowRect _lastTask;
    private uint _lastDpi;
    private int _curW, _curH, _curX, _curY;
    private bool _dragging;
    private int _dragAnchorX;
    private int _dragOffsetStart;

    public TaskbarOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000); // 嵌入任务栏前先藏在屏幕外
        MinimumSize = Size.Empty;
        MaximizeBox = MinimizeBox = false;
        Cursor = Cursors.SizeAll;

        var menu = new ContextMenuStrip();
        menu.Items.Add("设置…", null, (_, _) => App.ShowSettings());
        menu.Items.Add("回正任务栏位置", null, (_, _) =>
        {
            App.Config.Taskbar.OffsetX = 0;
            App.SaveConfig();
            RenderContent();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("隐藏任务栏显示", null, (_, _) =>
        {
            App.Config.Taskbar.Enabled = false;
            App.SaveConfig();
            App.ApplyTaskbarSettings();
        });
        ContextMenuStrip = menu;

        _poll.Tick += (_, _) => PollTaskbar();
        _refresh.Tick += (_, _) => { _refresh.Stop(); RefreshNow(); };
    }

    /// <summary>当前渲染的行（供测试断言）。</summary>
    public string[] LinesShown => _lines;

    /// <summary>当前内容来源文件（供测试断言）。</summary>
    public string SourcePath { get; private set; } = "";

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00080000   /* WS_EX_LAYERED */
                        | 0x08000000   /* WS_EX_NOACTIVATE：永不抢焦点 */
                        | 0x00000080;  /* WS_EX_TOOLWINDOW：不进 Alt+Tab */
            return cp;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e) { }

    // ---- 生命周期 ----

    /// <summary>按当前配置接管显示（启动与设置页改动时调用）。</summary>
    public void ApplyConfig()
    {
        Show(); // 先建句柄
        SetClickThrough(App.Config.Taskbar.ClickThrough);
        EmbedIntoTaskbar();
        RefreshNow();
        _poll.Stop();
        _poll.Start();
    }

    public void HideOverlay()
    {
        _poll.Stop();
        _refresh.Stop();
        Hide();
    }

    public void ScheduleRefresh()
    {
        if (IsDisposed || !App.Config.Taskbar.Enabled) return;
        _refresh.Stop();
        _refresh.Start();
    }

    public void SetClickThrough(bool on)
    {
        if (!IsHandleCreated) return;
        NativeMethods.ModifyExStyle(Handle,
            on ? NativeMethods.WS_EX_TRANSPARENT : 0,
            on ? 0 : NativeMethods.WS_EX_TRANSPARENT);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poll.Dispose();
            _refresh.Dispose();
            // 解除嵌入再销毁，避免销毁时仍挂在任务栏下
            if (IsHandleCreated && NativeMethods.GetParent(Handle) != IntPtr.Zero)
                NativeMethods.SetParent(Handle, IntPtr.Zero);
        }
        base.Dispose(disposing);
    }

    // ---- 内容 ----

    /// <summary>纯逻辑：取文本第 start..end 行（1 起算，闭区间）。</summary>
    internal static string[] SelectLines(string content, int start, int end)
    {
        start = Math.Max(1, start);
        end = Math.Max(start, end);
        var all = content.Replace("\r\n", "\n").Split('\n');
        int last = Math.Min(end, all.Length);
        if (start > last) return Array.Empty<string>();
        var result = new string[last - start + 1];
        for (int i = 0; i < result.Length; i++)
            result[i] = all[start - 1 + i].TrimEnd();
        return result;
    }

    private void RefreshNow()
    {
        if (IsDisposed || !App.Config.Taskbar.Enabled) return;
        var t = App.Config.Taskbar;
        var glass = App.Glasses.FirstOrDefault(g =>
                string.Equals(g.FilePath, t.File, StringComparison.OrdinalIgnoreCase))
            ?? App.Glasses.FirstOrDefault();
        SourcePath = glass?.FilePath ?? "";
        _lines = glass is null
            ? Array.Empty<string>()
            : SelectLines(glass.TextSnapshot, t.StartLine, t.EndLine);
        RenderContent();
    }

    // ---- 嵌入与跟随 ----

    private void EmbedIntoTaskbar()
    {
        if (IsDisposed) return;
        IntPtr tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return; // 资源管理器还没起来，交给轮询重试
        IntPtr hwnd = Handle;
        if (NativeMethods.GetParent(hwnd) == tray)
        {
            _dpi = NativeMethods.DpiForWindow(tray);
            return;
        }
        long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE,
            new IntPtr((style & ~NativeMethods.WS_POPUP) | NativeMethods.WS_CHILD));
        NativeMethods.SetParent(hwnd, tray);
        _dpi = NativeMethods.DpiForWindow(tray);
    }

    /// <summary>轮询：任务栏重建（重启资源管理器）、移动、DPI 变化时重新嵌入并重绘；
    /// 任务栏上其他内容（开关应用、任务栏工具变化）改变布局时仅重新挑空位。</summary>
    private void PollTaskbar()
    {
        if (IsDisposed) { _poll.Stop(); return; }
        if (!App.Config.Taskbar.Enabled) { Hide(); return; }
        EmbedIntoTaskbar();
        if (!IsHandleCreated || !Visible) return;
        IntPtr parent = NativeMethods.GetParent(Handle);
        if (parent == IntPtr.Zero) return;
        if (!NativeMethods.GetWindowRect(parent, out var rect)) return;
        uint dpi = NativeMethods.DpiForWindow(parent);
        bool rectChanged = rect.Left != _lastTask.Left || rect.Top != _lastTask.Top
            || rect.Right != _lastTask.Right || rect.Bottom != _lastTask.Bottom || dpi != _lastDpi;
        _lastTask = rect;
        _lastDpi = dpi;
        if (rectChanged)
        {
            _dpi = dpi;
            RenderContent();
            return;
        }
        Reposition(parent, rect);
    }

    // ---- 测量 / 渲染 ----

    private void RenderContent()
    {
        if (IsDisposed) return;
        if (_lines.Length == 0) { Hide(); return; }
        if (!IsHandleCreated) return;
        IntPtr parent = NativeMethods.GetParent(Handle);
        if (parent == IntPtr.Zero) return; // 还没嵌入任务栏，等轮询
        if (!NativeMethods.GetWindowRect(parent, out var task)) return;
        int taskW = task.Right - task.Left, taskH = task.Bottom - task.Top;
        if (taskW <= 0 || taskH <= 0) return;

        double scale = _dpi / 96.0;
        int padX = (int)Math.Round(PadX * scale);
        int padY = (int)Math.Round(PadY * scale);
        int availH = Math.Max(10, taskH - padY * 2);

        using var probe = new Bitmap(1, 1);
        using var g = Graphics.FromImage(probe);
        using var fmt = TextFormat();

        // 字号自适应：设置值是上限；放不下时缩小到可读下限，
        // 仍放不下就从起始行开始只显示放得下的行数——绝不把字压到看不清。
        var font = CreateFont(Math.Max(8f, App.Config.Taskbar.FontSize * _dpi / 72f));
        float lineH = font.GetHeight(g) * LineSpacing;
        try
        {
            if (_lines.Length * lineH > availH)
            {
                float lhMin = LineHeightAt(g, MinFontPx);
                int maxLines = Math.Max(1, (int)(availH / lhMin));
                if (_lines.Length > maxLines) _lines = _lines[..maxLines];
                float px = Math.Max(MinFontPx,
                    Math.Max(8f, App.Config.Taskbar.FontSize * _dpi / 72f) * availH / (_lines.Length * lineH));
                font.Dispose();
                font = CreateFont(px);
                lineH = font.GetHeight(g) * LineSpacing;
            }

            var widths = new float[_lines.Length];
            for (int i = 0; i < _lines.Length; i++)
                widths[i] = g.MeasureString(_lines[i], font, int.MaxValue, fmt).Width;
            int textW = (int)Math.Ceiling(widths.Max());
            int w = (int)Math.Clamp(
                textW + padX * 2L,
                Math.Max((int)Math.Round(MinWidth * scale), 1),
                Math.Min((long)Math.Round(MaxWidth * scale), (long)taskW - TrayGap * 2));
            int h = Math.Min(taskH, (int)Math.Ceiling(_lines.Length * lineH) + padY * 2);
            float maxTextWidth = w - padX * 2;

            using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var bg = Graphics.FromImage(bmp))
            {
                bg.SmoothingMode = SmoothingMode.AntiAlias;
                bg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                var ts = App.Config.Taskbar;
                var backColor = ColorUtil.Parse(ts.BackgroundColor, Color.FromArgb(31, 31, 31));
                int alpha = Math.Clamp(ts.BackgroundOpacityPercent, 0, 100) * 255 / 100;
                using (var brush = new SolidBrush(Color.FromArgb(alpha, backColor)))
                    bg.FillRectangle(brush, 0, 0, w, h);
                var textColor = ColorUtil.Parse(ts.TextColor, Color.White);
                int textAlpha = Math.Clamp(ts.TextOpacityPercent, 0, 100) * 255 / 100;
                using var textBrush = new SolidBrush(Color.FromArgb(textAlpha, textColor));
                float fy = (h - _lines.Length * lineH) / 2f;
                for (int i = 0; i < _lines.Length; i++)
                {
                    string line = Fit(_lines[i], bg, font, maxTextWidth, fmt);
                    bg.DrawString(line, font, textBrush,
                        new RectangleF(padX, fy, maxTextWidth, lineH), fmt);
                    fy += lineH;
                }
            }

            var (x, y) = ComputePosition(parent, task, taskW, taskH, w, h);
            _curW = w;
            _curH = h;
            _curX = x;
            _curY = y;
            Present(bmp, x, y);
            if (!Visible) Show();
        }
        finally
        {
            font.Dispose();
        }
    }

    private float LineHeightAt(Graphics g, float px)
    {
        using var f = CreateFont(px);
        return f.GetHeight(g) * LineSpacing;
    }

    private static StringFormat TextFormat() => new(StringFormatFlags.NoWrap)
    {
        Alignment = StringAlignment.Near,
        LineAlignment = StringAlignment.Center,
    };

    private Font CreateFont(float px)
    {
        string name = App.Config.Appearance.FontName;
        FontFamily family;
        try { family = new FontFamily(string.IsNullOrWhiteSpace(name) ? "微软雅黑" : name); }
        catch { family = FontFamily.GenericSansSerif; }
        return new Font(family, px, GraphicsUnit.Pixel);
    }

    /// <summary>超宽的行截断加省略号。</summary>
    private static string Fit(string line, Graphics g, Font font, float maxW, StringFormat fmt)
    {
        if (line.Length == 0 || g.MeasureString(line, font, int.MaxValue, fmt).Width <= maxW)
            return line;
        const string tail = "…";
        int lo = 0, hi = line.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (g.MeasureString(line[..mid] + tail, font, int.MaxValue, fmt).Width <= maxW) lo = mid;
            else hi = mid - 1;
        }
        return line[..lo] + tail;
    }

    // ---- 定位 ----

    private (int X, int Y) ComputePosition(IntPtr parent, NativeMethods.WindowRect task,
        int taskW, int taskH, int w, int h)
    {
        double scale = _dpi / 96.0;
        int margin = (int)Math.Round(4 * scale);
        int gap = (int)Math.Round(TrayGap * scale);
        int baseX = App.Config.Taskbar.Position switch
        {
            "tray" => TrayLeftClient(parent, taskW) - w - gap,
            "left" => margin,
            _ => FindFreeCenterX(parent, taskW, w, margin), // 默认：避开任务栏上已有内容，靠近中间
        };
        int x = Math.Clamp(baseX - Math.Clamp(App.Config.Taskbar.OffsetX, -4000, 8000),
            margin, Math.Max(margin, taskW - w - margin));
        int y = Math.Max(0, (taskH - h) / 2);
        return (x, y);
    }

    /// <summary>系统托盘区左缘（相对任务栏客户区）；找不到 TrayNotifyWnd 时按估计宽度退回。</summary>
    private int TrayLeftClient(IntPtr tray, int taskW)
    {
        var trayNotify = NativeMethods.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (trayNotify != IntPtr.Zero && NativeMethods.GetWindowRect(trayNotify, out var r)
            && r.Right > r.Left)
        {
            var pt = new Point(r.Left, 0);
            NativeMethods.ScreenToClient(tray, ref pt);
            return pt.X;
        }
        return taskW - (int)Math.Round(TrayFallback * _dpi / 96.0);
    }

    /// <summary>
    /// 枚举任务栏上所有可见子窗口（任务按钮区、托盘、TrafficMonitor 等任务栏工具）
    /// 得到已占用区间，在能容纳浮层的空隙里挑中点最靠近任务栏中心的那个；
    /// 找不到空隙时回退为正中（用户仍可拖动或改设置）。
    /// </summary>
    private int FindFreeCenterX(IntPtr tray, int taskW, int w, int margin)
    {
        var ranges = new List<(int L, int R)>();
        NativeMethods.EnumChildWindows(tray, (hwnd, _) =>
        {
            if (hwnd == Handle || !NativeMethods.IsWindowVisible(hwnd)) return true;
            if (!NativeMethods.GetWindowRect(hwnd, out var r)) return true;
            var l = new Point(r.Left, 0);
            var rt = new Point(r.Right, 0);
            NativeMethods.ScreenToClient(tray, ref l);
            NativeMethods.ScreenToClient(tray, ref rt);
            int left = Math.Max(0, l.X), right = Math.Min(taskW, rt.X);
            if (right - left > 0) ranges.Add((left, right));
            return true;
        }, IntPtr.Zero);

        ranges.Sort((a, b) => a.L.CompareTo(b.L));
        var merged = new List<(int L, int R)>();
        foreach (var (l, r) in ranges)
        {
            if (merged.Count > 0 && l <= merged[^1].R)
                merged[^1] = (merged[^1].L, Math.Max(merged[^1].R, r));
            else
                merged.Add((l, r));
        }

        var gaps = new List<(int L, int R)>();
        int cursor = 0;
        foreach (var (l, r) in merged)
        {
            if (l - cursor > 0) gaps.Add((cursor, l));
            cursor = Math.Max(cursor, r);
        }
        if (taskW - cursor > 0) gaps.Add((cursor, taskW));

        int bestX = taskW / 2 - w / 2; // 没有可用空隙时回退正中
        long bestDist = long.MaxValue;
        foreach (var (gl, gr) in gaps)
        {
            int lo = gl + margin, hi = gr - margin;
            if (hi - lo < w) continue;
            int x = Math.Clamp((gl + gr) / 2 - w / 2, lo, hi);
            long dist = Math.Abs((long)x + w / 2 - taskW / 2);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestX = x;
            }
        }
        return bestX;
    }

    /// <summary>内容与尺寸不变、但任务栏布局变化时，只挪位置不重绘。</summary>
    private void Reposition(IntPtr parent, NativeMethods.WindowRect task)
    {
        if (_curW <= 0 || _curH <= 0) return;
        int taskW = task.Right - task.Left, taskH = task.Bottom - task.Top;
        if (taskW <= 0 || taskH <= 0) return;
        var (x, y) = ComputePosition(parent, task, taskW, taskH, _curW, _curH);
        if (x == _curX && y == _curY) return;
        _curX = x;
        _curY = y;
        NativeMethods.SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0,
            0x0001 /* SWP_NOSIZE */ | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>逐像素 alpha 上屏；x/y 为相对任务栏客户区的坐标。</summary>
    private void Present(Bitmap bmp, int x, int y)
    {
        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
            try
            {
                IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0));
                try
                {
                    IntPtr old = NativeMethods.SelectObject(memDc, hbmp);
                    var dst = new NativeMethods.NativePoint(x, y);
                    var size = new NativeMethods.NativeSize(bmp.Width, bmp.Height);
                    var src = new NativeMethods.NativePoint(0, 0);
                    var blend = new NativeMethods.BlendFunction
                    {
                        BlendOp = 0,               // AC_SRC_OVER
                        BlendFlags = 0,
                        SourceConstantAlpha = 255,
                        AlphaFormat = 1,           // AC_SRC_ALPHA
                    };
                    NativeMethods.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size,
                        memDc, ref src, 0, ref blend, NativeMethods.ULW_ALPHA);
                    NativeMethods.SelectObject(memDc, old);
                }
                finally { NativeMethods.DeleteObject(hbmp); }
            }
            finally { NativeMethods.DeleteDC(memDc); }
        }
        finally { NativeMethods.ReleaseDC(IntPtr.Zero, screenDc); }
    }

    // ---- 拖动 / 穿透 ----

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || App.Config.Taskbar.ClickThrough) return;
        _dragging = true;
        _dragAnchorX = Cursor.Position.X;
        _dragOffsetStart = App.Config.Taskbar.OffsetX;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        int dx = _dragAnchorX - Cursor.Position.X; // 向左拖 = 离默认停靠点更远
        App.Config.Taskbar.OffsetX = Math.Clamp(_dragOffsetStart + dx, -4000, 8000);
        IntPtr parent = NativeMethods.GetParent(Handle);
        if (parent != IntPtr.Zero && NativeMethods.GetWindowRect(parent, out var task))
            Reposition(parent, task);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !_dragging) return;
        _dragging = false;
        Capture = false;
        App.SaveConfig();
    }
}
