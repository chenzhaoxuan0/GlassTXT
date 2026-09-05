namespace GlassTXT;

/// <summary>托盘图标与菜单：显示/隐藏、鼠标穿透、回正、设置、退出。</summary>
internal sealed class TrayController : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _clickThroughItem;

    public TrayController()
    {
        _menu = new ContextMenuStrip();
        _menu.Items.Add("显示/隐藏玻璃", null, (_, _) => App.ShowHideAll());

        _clickThroughItem = new ToolStripMenuItem("鼠标穿透") { CheckOnClick = true };
        _clickThroughItem.Click += (_, _) =>
        {
            App.ToggleClickThroughAll();
            _clickThroughItem.Checked = App.Glasses.Any(g => g.ClickThrough);
        };
        _menu.Items.Add(_clickThroughItem);

        _menu.Items.Add("回正位置", null, (_, _) => App.RecenterAll());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("设置…", null, (_, _) => App.ShowSettings());
        _menu.Items.Add("退出", null, (_, _) => App.Shutdown());
        _menu.Opening += (_, _) =>
            _clickThroughItem.Checked = App.Glasses.Any(g => g.ClickThrough);

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "GlassTXT 桌面玻璃便签",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => App.ShowHideAll();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const float r = 7f, edge = 31f;
            var path = new GraphicsPath();
            path.AddArc(1, 1, r * 2, r * 2, 180, 90);
            path.AddArc(edge - r * 2, 1, r * 2, r * 2, 270, 90);
            path.AddArc(edge - r * 2, edge - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(1, edge - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            using (var fill = new SolidBrush(Color.FromArgb(235, 88, 152, 245)))
                g.FillPath(fill, path);
            using (var pen = new Pen(Color.FromArgb(245, 255, 255, 255), 2f))
                g.DrawPath(pen, path);
            using (var shine = new Pen(Color.FromArgb(190, 255, 255, 255), 3f))
                g.DrawLine(shine, 9, 23, 23, 9);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
