namespace GlassTXT;

/// <summary>接收 WM_HOTKEY 的隐藏消息窗口；全局热键在这里注册。</summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int Id = 0xA001;

    private bool _registered;

    public HotkeyWindow()
    {
        var cp = new CreateParams { Caption = "GlassTXT-Hotkey" };
        CreateHandle(cp);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam.ToInt64() == Id)
            App.ToggleClickThroughAll();
        base.WndProc(ref m);
    }

    /// <summary>返回 "ok" / "empty" / "invalid" / "conflict"。</summary>
    public string Apply(string? hotkey)
    {
        if (_registered) NativeMethods.UnregisterHotKey(Handle, Id);
        _registered = false;

        string text = hotkey?.Trim() ?? "";
        if (text.Length == 0) return "empty";
        if (!Hotkey.TryParse(text, out uint mods, out uint vk)) return "invalid";
        _registered = NativeMethods.RegisterHotKey(Handle, Id, mods, vk);
        return _registered ? "ok" : "conflict";
    }

    public void Dispose()
    {
        if (_registered) NativeMethods.UnregisterHotKey(Handle, Id);
        _registered = false;
        DestroyHandle();
    }
}
