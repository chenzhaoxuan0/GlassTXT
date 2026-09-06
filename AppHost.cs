namespace GlassTXT;

/// <summary>WinForms owns the message loop, tray and application lifetime.</summary>
internal sealed class AppHost : ApplicationContext
{
    public AppHost(string file, string pipeName = App.PipeName)
    {
        App.Marshal = new Control();
        _ = App.Marshal.Handle;
        App.Tray = new TrayController();
        App.Hotkeys = new HotkeyWindow();
        App.ApplyHotkey(App.Config.Behavior.Hotkey);
        App.OpenGlass(file);
        App.StartPipeServer(pipeName);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            App.Shutdown();
            App.Hotkeys.Dispose();
            App.Tray.Dispose();
            App.Marshal.Dispose();
        }
        base.Dispose(disposing);
    }
}
