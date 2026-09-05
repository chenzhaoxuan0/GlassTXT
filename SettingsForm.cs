namespace GlassTXT;

/// <summary>设置页：外观全局生效、改动即时保存。热键在输入框内直接按键捕获。</summary>
internal sealed class SettingsForm : Form
{
    private readonly Button _glassColorBtn = MakeSwatch();
    private readonly Label _glassColorHex = MakeHex();
    private readonly TrackBar _opacityBar = new()
    {
        Minimum = 10, Maximum = 100, TickStyle = TickStyle.None,
        SmallChange = 1, LargeChange = 5, Width = 210,
    };
    private readonly Label _opacityVal = MakeHex();
    private readonly Button _fontColorBtn = MakeSwatch();
    private readonly Label _fontColorHex = MakeHex();
    private readonly ComboBox _fontBox = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, DropDownWidth = 280,
    };
    private readonly NumericUpDown _fontSize = new() { Minimum = 8, Maximum = 72, Width = 90 };
    private readonly TextBox _hotkeyBox = new()
    {
        ReadOnly = true, Width = 220,
        BackColor = Color.White, ForeColor = Color.Black, ShortcutsEnabled = false,
    };
    private readonly Label _hotkeyStatus = new() { AutoSize = true, Margin = new Padding(3, 4, 3, 3) };
    private readonly CheckBox _lockBox = new() { AutoSize = true, Text = "锁定位置（禁止拖动与缩放）" };
    private readonly CheckBox _autoStartBox = new() { AutoSize = true, Text = "开机自启（当前用户）" };
    private bool _loading = true;

    public SettingsForm()
    {
        Text = "GlassTXT 设置";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        ClientSize = new Size(500, 486);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(14, 12, 14, 10),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Heading(string text)
        {
            var label = new Label
            {
                Text = text,
                AutoSize = true,
                Margin = new Padding(0, 10, 0, 2),
                Font = new Font(SystemFonts.DialogFont, FontStyle.Bold),
            };
            table.Controls.Add(label);
            table.SetColumnSpan(label, 2);
        }

        void Row(string text, Control control)
        {
            table.Controls.Add(new Label
            {
                Text = text,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 8, 8, 2),
            });
            table.Controls.Add(control);
        }

        Heading("外观");
        Row("玻璃颜色", SwatchRow(_glassColorBtn, _glassColorHex));
        Row("不透明度", OpacityRow());
        Row("字体颜色", SwatchRow(_fontColorBtn, _fontColorHex));
        Row("字体", _fontBox);
        Row("字号", _fontSize);

        Heading("行为");
        Row("穿透热键", _hotkeyBox);
        Row("", _hotkeyStatus);
        Row("", _lockBox);
        Row("", _autoStartBox);
        Row("", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(350, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 10, 0, 0),
            Text = "所有改动即时生效并自动保存。设置热键：点击热键输入框，直接按下想要的组合键；按退格键清除热键。若提示被占用，请换一个组合键。",
        });

        Controls.Add(table);

        // 初值
        var a = App.Config.Appearance;
        _opacityBar.Value = Math.Clamp(a.OpacityPercent, 10, 100);
        _fontSize.Value = Math.Clamp(a.FontSize, 8, 72);
        foreach (var family in new System.Drawing.Text.InstalledFontCollection().Families.Select(f => f.Name))
            _fontBox.Items.Add(family);
        if (!_fontBox.Items.Contains(a.FontName)) _fontBox.Items.Add(a.FontName);
        _fontBox.SelectedItem = a.FontName;
        _hotkeyBox.Text = App.Config.Behavior.Hotkey;
        _lockBox.Checked = App.Config.Behavior.LockPosition;
        _autoStartBox.Checked = AutoStart.IsSet();
        RefreshVisuals();
        _hotkeyStatus.Text = StatusText(App.LastHotkeyStatus, _hotkeyBox.Text, out var statusColor);
        _hotkeyStatus.ForeColor = statusColor;

        // 事件
        _glassColorBtn.Click += (_, _) =>
            PickColor(App.Config.Appearance.GlassColor, c => App.Config.Appearance.GlassColor = ColorUtil.ToHex(c));
        _fontColorBtn.Click += (_, _) =>
            PickColor(App.Config.Appearance.FontColor, c => App.Config.Appearance.FontColor = ColorUtil.ToHex(c));
        _opacityBar.ValueChanged += (_, _) =>
        {
            App.Config.Appearance.OpacityPercent = _opacityBar.Value;
            ApplyAppearanceChanges();
        };
        _fontBox.SelectedIndexChanged += (_, _) =>
        {
            if (_fontBox.SelectedItem is string name)
            {
                App.Config.Appearance.FontName = name;
                ApplyAppearanceChanges();
            }
        };
        _fontSize.ValueChanged += (_, _) =>
        {
            App.Config.Appearance.FontSize = (int)_fontSize.Value;
            ApplyAppearanceChanges();
        };
        _lockBox.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            App.Config.Behavior.LockPosition = _lockBox.Checked;
            App.SaveConfig();
        };
        _autoStartBox.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            AutoStart.Set(_autoStartBox.Checked);
            App.Config.Behavior.AutoStart = _autoStartBox.Checked;
            App.SaveConfig();
        };
        _hotkeyBox.KeyDown += HotkeyKeyDown;
        _hotkeyBox.KeyPress += (_, e) => e.Handled = true;

        FormClosed += (_, _) => App.SettingsWindow = null;
        _loading = false;
    }

    // ---- 小控件 ----

    private static Button MakeSwatch() => new()
    {
        Width = 84, Height = 26,
        FlatStyle = FlatStyle.Flat,
        FlatAppearance = { BorderSize = 1, BorderColor = Color.Gray },
    };

    private static Label MakeHex() => new() { AutoSize = true, Anchor = AnchorStyles.Left };

    private FlowLayoutPanel SwatchRow(Button button, Label hex)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(3, 5, 3, 3),
        };
        panel.Controls.Add(button);
        panel.Controls.Add(new Label { Text = "  ", AutoSize = true });
        panel.Controls.Add(hex);
        return panel;
    }

    private FlowLayoutPanel OpacityRow()
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(3, 5, 3, 3),
        };
        _opacityBar.Margin = new Padding(0, 4, 8, 0);
        panel.Controls.Add(_opacityBar);
        panel.Controls.Add(_opacityVal);
        return panel;
    }

    // ---- 逻辑 ----

    private void PickColor(string currentHtml, Action<Color> assign)
    {
        using var dialog = new ColorDialog
        {
            FullOpen = true,
            Color = ColorUtil.Parse(currentHtml, Color.White),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        assign(dialog.Color);
        ApplyAppearanceChanges();
    }

    private void ApplyAppearanceChanges()
    {
        RefreshVisuals();
        App.ApplyAppearanceToAll();
        App.SaveConfig();
    }

    private void RefreshVisuals()
    {
        var glass = ColorUtil.Parse(App.Config.Appearance.GlassColor, Color.Black);
        var font = ColorUtil.Parse(App.Config.Appearance.FontColor, Color.White);
        _glassColorBtn.BackColor = glass;
        _glassColorHex.Text = ColorUtil.ToHex(glass);
        _fontColorBtn.BackColor = font;
        _fontColorHex.Text = ColorUtil.ToHex(font);
        _opacityVal.Text = _opacityBar.Value + "%";
    }

    private void HotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        var key = e.KeyCode;
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin or Keys.None)
            return;
        if (key is Keys.Back or Keys.Delete)
        {
            CommitHotkey("");
            return;
        }
        var parts = new List<string>();
        if (e.Control) parts.Add("Ctrl");
        if (e.Alt) parts.Add("Alt");
        if (e.Shift) parts.Add("Shift");
        if (NativeMethods.IsWinKeyDown()) parts.Add("Win");
        if (parts.Count == 0) return; // 必须带修饰键
        parts.Add(KeyToken(key));
        CommitHotkey(string.Join("+", parts));
    }

    private static string KeyToken(Keys key)
    {
        int code = (int)(key & Keys.KeyCode);
        if (code is >= (int)Keys.A and <= (int)Keys.Z)
            return ((char)('A' + code - (int)Keys.A)).ToString();
        if (code is >= (int)Keys.D0 and <= (int)Keys.D9)
            return ((char)('0' + code - (int)Keys.D0)).ToString();
        if (code is >= (int)Keys.NumPad0 and <= (int)Keys.NumPad9)
            return "NumPad" + (code - (int)Keys.NumPad0);
        return key.ToString();
    }

    private void CommitHotkey(string hotkey)
    {
        _hotkeyBox.Text = hotkey;
        App.Config.Behavior.Hotkey = hotkey;
        string status = App.ApplyHotkey(hotkey);
        _hotkeyStatus.Text = StatusText(status, hotkey, out var color);
        _hotkeyStatus.ForeColor = color;
        App.SaveConfig();
    }

    private static string StatusText(string status, string hotkey, out Color color)
    {
        switch (status)
        {
            case "ok":
                color = Color.ForestGreen;
                return $"已生效：{hotkey}";
            case "empty":
                color = Color.DimGray;
                return "未设置热键";
            case "invalid":
                color = Color.Firebrick;
                return "组合键无效：需要 修饰键 + 主键，例如 Ctrl+Alt+G";
            default:
                color = Color.Firebrick;
                return "注册失败：组合键已被其他程序占用，请换一个";
        }
    }
}
