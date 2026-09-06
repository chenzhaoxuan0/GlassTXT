using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassTXT;
using Forms = System.Windows.Forms;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;

internal static class Program
{
    private static readonly string Output = Path.Combine(AppContext.BaseDirectory, "test-output");
    private static readonly string Pipe = "GlassTXT-Tests-" + Guid.NewGuid().ToString("N");
    private static int _passed;
    private static bool _finished;

    [STAThread]
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(Output);
        Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
        Forms.Application.EnableVisualStyles();
        Forms.Application.SetCompatibleTextRenderingDefault(false);
        Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
        App.Config = new Config();
        App.Config.Behavior.Hotkey = "";
        var file = Path.Combine(Output, "editing.txt");
        File.WriteAllText(file, "Initial text");
        App.Config.SetLayout(file, new WindowLayout { X = 180, Y = 160, Width = 420, Height = 460 });

        using var host = new AppHost(file, Pipe);
        if (args.Contains("--interactive"))
        {
            var window = App.Glasses.Single();
            window.ShowInTaskbar = true;
            window.Title = "GlassTXT Interactive Test";
            window.ShowAndActivate();
            Console.WriteLine($"Interactive fixture: {file}");
            Console.WriteLine("Close the glass to end this test session.");
            Forms.Application.Run(host);
            return 0;
        }
        // Programmatic edits still work; live desktop typing cannot corrupt fixtures.
        Field<TextBox>(App.Glasses.Single(), "_box").IsReadOnly = true;
        using var watchdog = new Forms.Timer { Interval = 30000 };
        watchdog.Tick += (_, _) =>
        {
            Console.Error.WriteLine("FAIL: test watchdog expired");
            Environment.ExitCode = 1;
            App.Shutdown();
        };
        watchdog.Start();
        App.Marshal.BeginInvoke(async () =>
        {
            try
            {
                await RunTests(file);
                _finished = true;
                Console.WriteLine($"PASS: {_passed} checks");
            }
            catch (Exception ex)
            {
                Environment.ExitCode = 1;
                Console.Error.WriteLine(ex);
                App.Shutdown();
            }
        });
        Forms.Application.Run(host);
        return _finished ? Environment.ExitCode : 1;
    }

    private static async Task RunTests(string file)
    {
        await Task.Delay(200);
        CheckFirstRunFile();
        Check(Forms.Application.MessageLoop, "WinForms owns the message loop");
        Check(System.Windows.Application.Current is null, "No WPF Application or second main loop");
        var glass = App.Glasses.Single();
        var box = Field<TextBox>(glass, "_box");
        var root = Field<Grid>(glass, "_root");
        var hwnd = new WindowInteropHelper(glass).Handle;
        NativeMethods.GetWindowRect(hwnd, out var rect);
        Check(rect.Left == 180 && rect.Top == 160 && rect.Right - rect.Left == 420,
            "v1.1 physical-pixel layout restored");
        Check(Math.Abs(box.FontSize - 14 * 96.0 / 72) < 0.01, "v1.1 point-based font size preserved");

        box.AppendText("\nASCII / \u4e2d\u6587 / \ud83d\ude00");
        await Task.Delay(750);
        Check(File.ReadAllText(file) == box.Text, "WPF debounce saves Unicode under WinForms loop");
        Check(!Field<bool>(glass, "_dirty"), "Successful save clears dirty state");

        box.AppendText("\nSaved on hide");
        App.ShowHideAll();
        Check(!glass.IsVisible && File.ReadAllText(file) == box.Text, "Hide flushes pending edits");
        App.ShowHideAll();
        Check(glass.IsVisible, "Tray show restores WPF glass");

        await Task.Delay(1000);
        File.WriteAllText(file, "External update");
        for (int i = 0; i < 40 && box.Text != "External update"; i++) await Task.Delay(50);
        Check(box.Text == "External update", "External file change reloads on UI thread");

        App.ShowSettings();
        Check(App.SettingsWindow is { IsDisposed: false, Visible: true }, "Settings remain modeless WinForms");
        CheckOpacityInputs(App.SettingsWindow!, glass);
        App.SettingsWindow!.Close();
        glass.ShowAndActivate();

        box.Text = string.Join("\r\n", Enumerable.Range(1, 160).Select(i => $"Line {i:000} - scrolling fixture"));
        box.UpdateLayout();
        await Task.Delay(150);
        box.ScrollToVerticalOffset(250);
        await Task.Delay(100);
        double before = box.VerticalOffset;
        Invoke(glass, "HandleMouseWheel", 120, false);
        await Task.Delay(100);
        Check(box.VerticalOffset < before, "Positive wheel scrolls upward");
        before = box.VerticalOffset;
        Invoke(glass, "HandleMouseWheel", -120, false);
        await Task.Delay(100);
        Check(box.VerticalOffset > before, "Negative wheel scrolls downward");
        before = box.VerticalOffset;
        Invoke(glass, "HandleMouseWheel", 60, false);
        Check(box.VerticalOffset == before, "High-resolution wheel accumulates partial notch");
        Invoke(glass, "HandleMouseWheel", 60, false);
        await Task.Delay(100);
        Check(box.VerticalOffset < before, "Two partial wheel deltas produce one notch");

        int zoom = App.Config.Appearance.ZoomPercent;
        Invoke(glass, "HandleMouseWheel", 120, true);
        Check(App.Config.Appearance.ZoomPercent == zoom + 10, "Ctrl-wheel changes global zoom");
        Check(Field<ZoomBadge>(glass, "_zoomBadge").Visible, "Zoom badge appears");
        App.Config.Appearance.ZoomPercent = 300;
        Invoke(glass, "HandleMouseWheel", 120, true);
        Check(App.Config.Appearance.ZoomPercent == 300, "Zoom upper bound");
        App.Config.Appearance.ZoomPercent = 50;
        Invoke(glass, "HandleMouseWheel", -120, true);
        Check(App.Config.Appearance.ZoomPercent == 50, "Zoom lower bound");
        App.Config.Appearance.ZoomPercent = 100;
        App.ApplyAppearanceToAll();
        box.ScrollToVerticalOffset(0);
        await Task.Delay(100);

        App.Config.Behavior.MiddleScrollMode = "hold";
        App.ApplyBehaviorToAll();
        RaiseMouse(box, UIElement.PreviewMouseDownEvent, MouseButton.Middle);
        Check(Field<bool>(glass, "_autoScrolling") && box.IsMouseCaptured, "Preview middle-down starts hold scroll");
        var anchor = Field<Point>(glass, "_autoScrollAnchor");
        SetField(glass, "_autoScrollOffset", new Point(anchor.X, anchor.Y + 500));
        Invoke(glass, "AutoScrollTick");
        await Task.Delay(100);
        Check(box.VerticalOffset > 0, "Middle-scroll timer advances text");
        RaiseMouse(box, UIElement.PreviewMouseUpEvent, MouseButton.Middle);
        Check(!Field<bool>(glass, "_autoScrolling") && !box.IsMouseCaptured, "Middle-up releases hold capture");

        App.Config.Behavior.MiddleScrollMode = "toggle";
        App.ApplyBehaviorToAll();
        RaiseMouse(box, UIElement.PreviewMouseDownEvent, MouseButton.Middle);
        RaiseMouse(box, UIElement.PreviewMouseUpEvent, MouseButton.Middle);
        Check(Field<bool>(glass, "_autoScrolling"), "Toggle mode survives middle-up");
        box.ReleaseMouseCapture();
        Check(!Field<bool>(glass, "_autoScrolling"), "Lost capture stops scrolling without recapture");
        RaiseMouse(box, UIElement.PreviewMouseDownEvent, MouseButton.Middle);
        box.RaiseEvent(new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(glass), Environment.TickCount, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Check(!Field<bool>(glass, "_autoScrolling"), "Escape stops toggle scrolling");
        RaiseMouse(box, UIElement.PreviewMouseDownEvent, MouseButton.Middle);
        glass.Hide();
        Check(!Field<bool>(glass, "_autoScrolling"), "Hide releases middle-scroll capture");
        glass.ShowAndActivate();
        RaiseMouse(box, UIElement.PreviewMouseDownEvent, MouseButton.Middle);
        App.Config.Behavior.MiddleScrollMode = "hold";
        App.ApplyBehaviorToAll();
        Check(!Field<bool>(glass, "_autoScrolling"), "Changing scroll mode stops active scroll");
        RaiseMouse(box, UIElement.PreviewMouseDownEvent, MouseButton.Middle);
        glass.SetClickThrough(true);
        Check(!Field<bool>(glass, "_autoScrolling"), "Click-through stops active scrolling");
        Check((NativeMethods.GetWindowLongPtr(hwnd, -20).ToInt64() & 0x20) != 0, "Click-through sets native style");
        glass.SetClickThrough(false);
        Check((NativeMethods.GetWindowLongPtr(hwnd, -20).ToInt64() & 0x20) == 0, "Click-through clears native style");
        App.Config.Behavior.MiddleScrollMode = "off";
        App.ApplyBehaviorToAll();
        RaiseMouse(box, UIElement.PreviewMouseDownEvent, MouseButton.Middle);
        Check(!Field<bool>(glass, "_autoScrolling"), "Off mode ignores middle-down");

        CheckResize(glass, hwnd);
        CheckAlpha(glass, box, root);

        var second = Path.Combine(Output, "second.txt");
        File.WriteAllText(second, "Second glass");
        using (var client = new NamedPipeClientStream(".", Pipe, PipeDirection.Out))
        {
            await client.ConnectAsync(2000);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            await writer.WriteLineAsync("open\t" + second);
        }
        await Task.Delay(300);
        Check(App.Glasses.Count == 2, "Named pipe dispatches through WinForms to open another glass");
        App.OpenGlass(second);
        Check(App.Glasses.Count == 2, "Opening same file reuses glass");
        var other = App.Glasses.Single(g => g.FilePath == second);
        var otherBox = Field<TextBox>(other, "_box");
        otherBox.AppendText(" - immediate close");
        string expected = otherBox.Text;
        other.Close();
        Check(File.ReadAllText(second) == expected, "Close flushes before the debounce deadline");
        Check(App.Glasses.Count == 1 && Forms.Application.MessageLoop, "Closing one glass keeps shell alive");

        box.Text = "Last glass - final pending edit";
        glass.Close();
        Check(File.ReadAllText(file) == "Last glass - final pending edit", "Last-glass close saves pending text");
        Check(App.Glasses.Count == 0, "Last-glass close removes all WPF windows");
        Check(App.Config.FindLayout(file) is { Width: > 0, Height: > 0 }, "Closing persists valid layout");
        Check(Field<ZoomBadge>(glass, "_zoomBadge").IsDisposed, "Closing disposes overlay");
        Check(!Field<System.Windows.Threading.DispatcherTimer>(glass, "_saveTimer").IsEnabled,
            "Closing stops save timer");
    }

    private static void CheckFirstRunFile()
    {
        var entry = typeof(App).Assembly.GetType("GlassTXT.Program")!;
        var resolve = entry.GetMethod("ResolveFile", BindingFlags.Static | BindingFlags.NonPublic)!;
        var sample = (string)entry.GetField("SampleTodo", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetRawConstantValue()!;
        Check(sample.Contains("GlassTXT 1.3") && sample.Contains("Ctrl+Alt+G")
            && sample.Contains("0%-100%"), "First-run template includes version and usage instructions");
        var defaultPath = Path.Combine(AppContext.BaseDirectory, "todo.txt");
        var existing = File.Exists(defaultPath) ? File.ReadAllBytes(defaultPath) : null;
        var resolved = (string)resolve.Invoke(null, [Array.Empty<string>()])!;
        Check(resolved == defaultPath && File.Exists(defaultPath), "No-argument launch creates default TXT beside executable");
        Check(existing is null ? File.ReadAllText(defaultPath) == sample
            : existing.SequenceEqual(File.ReadAllBytes(defaultPath)), "First run writes instructions without replacing existing TXT");
        var before = File.ReadAllBytes(defaultPath);
        resolve.Invoke(null, [Array.Empty<string>()]);
        Check(before.SequenceEqual(File.ReadAllBytes(defaultPath)), "Repeated launch leaves default TXT unchanged");
    }

    private static void CheckResize(GlassWindow glass, IntPtr hwnd)
    {
        double w = glass.ActualWidth, h = glass.ActualHeight;
        (double x, double y, int hit)[] edges =
        [
            (1, 1, 13), (w / 2, 1, 12), (w - 1, 1, 14),
            (1, h / 2, 10), (w - 1, h / 2, 11),
            (1, h - 1, 16), (w / 2, h - 1, 15), (w - 1, h - 1, 17),
        ];
        foreach (var (x, y, hit) in edges)
        {
            var screen = glass.PointToScreen(new Point(x, y));
            int packed = ((int)screen.X & 0xffff) | (((int)screen.Y & 0xffff) << 16);
            object[] args = [hwnd, 0x84, IntPtr.Zero, new IntPtr(packed), false];
            var result = (IntPtr)Method("WndProcHook").Invoke(glass, args)!;
            Check((bool)args[4] && result.ToInt32() == hit, $"Resize hit test {hit}");
        }
        App.Config.Behavior.LockPosition = true;
        var corner = glass.PointToScreen(new Point(1, 1));
        object[] lockedArgs = [hwnd, 0x84, IntPtr.Zero,
            new IntPtr(((int)corner.X & 0xffff) | (((int)corner.Y & 0xffff) << 16)), false];
        Method("WndProcHook").Invoke(glass, lockedArgs);
        Check(!(bool)lockedArgs[4], "Lock position disables resize hit test");
        App.Config.Behavior.LockPosition = false;
    }

    private static void CheckAlpha(GlassWindow glass, TextBox box, Grid root)
    {
        box.Text = "MMMMMMMM\nIndependent alpha";
        box.Select(0, 0);
        box.ScrollToHome();
        App.Config.Appearance.GlassColor = "#204060";
        App.Config.Appearance.FontColor = "#F0F0F0";
        App.Config.Appearance.FontSize = 30;
        App.Config.Appearance.OpacityPercent = 25;
        App.Config.Appearance.TextOpacityPercent = 100;
        glass.ApplyAppearance();
        root.UpdateLayout();
        var lowBackground = Render(root, "alpha-background-25.png");
        App.Config.Appearance.OpacityPercent = 75;
        glass.ApplyAppearance();
        root.UpdateLayout();
        var highBackground = Render(root, "alpha-background-75.png");
        int sample = ((5 * (int)root.ActualWidth) + 5) * 4;
        Check(lowBackground[sample + 3] == 63 && highBackground[sample + 3] == 191,
            "Background and drag strip use only glass alpha");
        var opaqueText = Enumerable.Range(0, lowBackground.Length / 4)
            .Where(i => lowBackground[i * 4 + 3] == 255).ToArray();
        Check(opaqueText.Length > 20, "Rendered text contains fully opaque glyph pixels");
        Check(opaqueText.All(i => highBackground[i * 4 + 3] == 255
            && highBackground[i * 4] == lowBackground[i * 4]),
            "Opaque glyphs remain unchanged when glass alpha changes");
        App.Config.Appearance.TextOpacityPercent = 30;
        glass.ApplyAppearance();
        root.UpdateLayout();
        var lowText = Render(root, "alpha-text-30.png");
        Check(lowText[sample + 3] == highBackground[sample + 3], "Text alpha leaves background unchanged");
        Check(opaqueText.Count(i => lowText[i * 4 + 3] < 230) > opaqueText.Length * 0.9,
            "Text alpha composites through to the background");
        Check(glass.Opacity == 1, "Window-level opacity remains one");

        App.Config.Appearance.OpacityPercent = 0;
        App.Config.Appearance.TextOpacityPercent = 100;
        glass.ApplyAppearance();
        root.UpdateLayout();
        var noBackground = Render(root, "alpha-background-0.png");
        Check(noBackground[sample + 3] == 0, "Zero glass opacity renders truly transparent background");
        Check(opaqueText.All(i => noBackground[i * 4 + 3] == 255),
            "Zero glass opacity preserves opaque text");
        App.Config.Appearance.OpacityPercent = 75;
        App.Config.Appearance.TextOpacityPercent = 0;
        glass.ApplyAppearance();
        root.UpdateLayout();
        var noText = Render(root, "alpha-text-0.png");
        Check(opaqueText.All(i => noText[i * 4 + 3] == 191),
            "Zero text opacity removes glyphs without changing glass alpha");
        App.Config.Appearance.OpacityPercent = 0;
        glass.ApplyAppearance();
        root.UpdateLayout();
        var invisible = Render(root, "alpha-both-0.png");
        Check(Enumerable.Range(0, invisible.Length / 4).All(i => invisible[i * 4 + 3] == 0),
            "Both zero opacities render a fully transparent surface");
        App.Config.Appearance.OpacityPercent = -10;
        App.Config.Appearance.TextOpacityPercent = 150;
        glass.ApplyAppearance();
        Check(((SolidColorBrush)root.Background).Color.A == 0 && box.Foreground.Opacity == 1,
            "Out-of-range config opacities are clamped to zero and one");
        App.Config.Appearance.OpacityPercent = 78;
        App.Config.Appearance.TextOpacityPercent = 100;
        glass.ApplyAppearance();
    }

    private static void CheckOpacityInputs(SettingsForm settings, GlassWindow glass)
    {
        var a = App.Config.Appearance;
        int initialGlass = a.OpacityPercent, initialText = a.TextOpacityPercent;
        var box = Field<TextBox>(glass, "_box");
        var root = Field<Grid>(glass, "_root");
        var glassBar = Field<Forms.TrackBar>(settings, "_glassOpacityBar");
        var textBar = Field<Forms.TrackBar>(settings, "_textOpacityBar");
        var glassInput = Field<Forms.NumericUpDown>(settings, "_glassOpacityInput");
        var textInput = Field<Forms.NumericUpDown>(settings, "_textOpacityInput");
        Check(glassBar.Minimum == 0 && textBar.Minimum == 0
            && glassInput.Minimum == 0 && textInput.Minimum == 0
            && glassInput.Maximum == 100 && textInput.Maximum == 100,
            "Both sliders and numeric inputs accept zero to 100");
        Check(glassInput.Value == initialGlass && textInput.Value == initialText,
            "Opacity inputs initialize from configuration");
        glassBar.Value = 0;
        Check(glassInput.Value == 0 && a.OpacityPercent == 0
            && ((SolidColorBrush)root.Background).Color.A == 0,
            "Glass slider synchronizes input, config and live zero-alpha rendering");
        textInput.Value = 0;
        Check(textBar.Value == 0 && a.TextOpacityPercent == 0 && box.Foreground.Opacity == 0,
            "Text numeric input synchronizes slider, config and live zero-alpha rendering");
        var saved = Config.Load();
        Check(saved.Appearance.OpacityPercent == 0 && saved.Appearance.TextOpacityPercent == 0,
            "Zero opacities persist without fallback to the default");
        using (var reopened = new SettingsForm())
        {
            Check(Field<Forms.NumericUpDown>(reopened, "_glassOpacityInput").Value == 0
                && Field<Forms.NumericUpDown>(reopened, "_textOpacityInput").Value == 0,
                "Reopened settings retain both zero values");
        }

        EnterNumber(glassInput, "37");
        Check(glassBar.Value == 37 && a.OpacityPercent == 37 && a.TextOpacityPercent == 0,
            "Typing glass percentage and Enter updates only glass opacity");
        EnterNumber(textInput, "62");
        Check(textBar.Value == 62 && a.TextOpacityPercent == 62 && a.OpacityPercent == 37,
            "Typing text percentage and Enter updates only text opacity");
        EnterNumber(glassInput, "150");
        Check(glassInput.Value == 100 && glassBar.Value == 100 && a.OpacityPercent == 100,
            "Typed opacity above 100 is clamped");
        EnterNumber(textInput, "-5");
        Check(textInput.Value == 0 && textBar.Value == 0 && a.TextOpacityPercent == 0,
            "Typed negative opacity is clamped");
        EnterNumber(textInput, "abc");
        Check(textInput.Value == 0 && a.TextOpacityPercent == 0,
            "Invalid opacity text retains the last valid value");
        EnterNumber(textInput, "");
        Check(textInput.Value == 0 && a.TextOpacityPercent == 0,
            "Empty opacity text does not corrupt the value");
        textBar.Value = 100;
        Check(textInput.Value == 100 && a.TextOpacityPercent == 100,
            "Text slider synchronizes numeric input at upper endpoint");
        glassInput.Focus();
        glassInput.Text = "48";
        textInput.Focus();
        Check(glassBar.Value == 48 && a.OpacityPercent == 48,
            "Leaving numeric input commits typed opacity");

        glassInput.Value = initialGlass;
        textInput.Value = initialText;
        settings.PerformLayout();
        foreach (var input in new[] { glassInput, textInput })
        {
            var row = input.Parent!;
            Check(input.Right <= row.ClientSize.Width && input.Bottom <= row.ClientSize.Height
                && row.Right <= row.Parent!.ClientSize.Width,
                $"{input.AccessibleName}: numeric input fits inside settings row");
        }
        using var bitmap = new System.Drawing.Bitmap(settings.Width, settings.Height);
        settings.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
        bitmap.Save(Path.Combine(Output, "settings-opacity-inputs.png"));
    }

    private static void EnterNumber(Forms.NumericUpDown input, string text)
    {
        input.Text = text;
        typeof(Forms.Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(input, [new Forms.KeyEventArgs(Forms.Keys.Enter)]);
    }

    private static byte[] Render(Grid root, string file)
    {
        int width = (int)root.ActualWidth, height = (int)root.ActualHeight;
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(Output, file));
        encoder.Save(stream);
        return pixels;
    }

    private static void RaiseMouse(TextBox box, RoutedEvent routedEvent, MouseButton button)
        => box.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button)
        { RoutedEvent = routedEvent });

    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void SetField(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static MethodInfo Method(string name)
        => typeof(GlassWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void Invoke(GlassWindow target, string name, params object[] args)
        => Method(name).Invoke(target, args);

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        _passed++;
        Console.WriteLine("PASS: " + name);
    }
}
