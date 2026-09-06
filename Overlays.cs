namespace GlassTXT;

/// <summary>不抢焦点、鼠标点击穿透的小浮层基类（用于缩放倍率徽标）。</summary>
internal abstract class OverlayForm : Form
{
    protected OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        MinimizeBox = MaximizeBox = false;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000008   /* WS_EX_TOPMOST */
                        | 0x00000020   /* WS_EX_TRANSPARENT：点击与滚轮穿透 */
                        | 0x08000000   /* WS_EX_NOACTIVATE */
                        | 0x00000080;  /* WS_EX_TOOLWINDOW：不进 Alt+Tab */
            return cp;
        }
    }
}

/// <summary>Ctrl+滚轮缩放时短暂显示的倍率徽标，例如"缩放 120%"。</summary>
internal sealed class ZoomBadge : OverlayForm
{
    private readonly Label _label = new()
    {
        AutoSize = true,
        BackColor = Color.FromArgb(225, 20, 20, 20),
        ForeColor = Color.White,
        Font = new Font("微软雅黑", 10.5f, FontStyle.Bold),
        Padding = new Padding(10, 6, 10, 6),
    };
    private readonly Timer _hide = new() { Interval = 900, Enabled = false };

    public ZoomBadge()
    {
        Controls.Add(_label);
        _hide.Tick += (_, _) => Hide();
    }

    public void ShowAt(int x, int y, string text)
    {
        _label.Text = text;
        Size = _label.GetPreferredSize(Size.Empty);
        Location = new System.Drawing.Point(x, y);
        _hide.Stop();
        _hide.Start();
        Show();
    }
}
