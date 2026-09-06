using System.Runtime.InteropServices;

namespace GlassTXT;

internal static class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int HT_CAPTION = 0x0002;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public static void ModifyExStyle(IntPtr hwnd, int add, int remove)
    {
        uint style = (uint)GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style = (style | (uint)add) & ~(uint)remove;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr((long)style));
    }

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct WindowRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    public const int GWL_STYLE = -16;
    public const long WS_CHILD = 0x40000000;
    public const long WS_POPUP = 0x80000000;
    public const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? windowName);

    [DllImport("user32.dll")]
    public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    public delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr lParam);

    /// <summary>取窗口所在显示器的 DPI；取不到时回退 96。</summary>
    public static uint DpiForWindow(IntPtr hwnd)
    {
        try
        {
            uint dpi = GetDpiForWindow(hwnd);
            return dpi >= 96 ? dpi : 96;
        }
        catch { return 96; }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ScreenToClient(IntPtr hwnd, ref Point pt);

    // ---- UpdateLayeredWindow：32 位 ARGB 逐像素 alpha 浮层 ----

    public const uint ULW_ALPHA = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct NativePoint { public int X, Y; public NativePoint(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeSize { public int Width, Height; public NativeSize(int w, int h) { Width = w; Height = h; } }

    [StructLayout(LayoutKind.Sequential)]
    public struct BlendFunction
    {
        public byte BlendOp;           // 0 = AC_SRC_OVER
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;       // 1 = AC_SRC_ALPHA（按位 alpha 混合）
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref NativePoint dst,
        ref NativeSize size, IntPtr hdcSrc, ref NativePoint src, uint crKey, ref BlendFunction blend, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern short GetKeyState(int vk);

    public static bool IsWinKeyDown()
        => (GetKeyState(0x5B /* VK_LWIN */) & 0x8000) != 0
           || (GetKeyState(0x5C /* VK_RWIN */) & 0x8000) != 0;

    /// <summary>设置窗口的 Accent 合成层：让背景以独立透明度叠加在桌面上（文字不受影响）。</summary>
    public static void SetGlassAccent(IntPtr hwnd, bool enabled, int alphaPercent, Color color)
    {
        var policy = new AccentPolicy { AccentState = enabled ? 4 : 0 }; // 4 = ACCENT_ENABLE_ACRYLICBLURBEHIND
        if (enabled)
        {
            int a = Math.Clamp(alphaPercent, 0, 100) * 255 / 100;
            policy.GradientColor = (a << 24) | (color.B << 16) | (color.G << 8) | color.R;
        }
        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
        try
        {
            Marshal.StructureToPtr(policy, ptr, false);
            var data = new WindowCompositionAttribData
            {
                Attribute = 19, // WCA_ACCENT_POLICY
                Data = ptr,
                SizeOfData = Marshal.SizeOf<AccentPolicy>(),
            };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int AccentState; public int AccentFlags; public int GradientColor; public int AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttribData { public int Attribute; public IntPtr Data; public int SizeOfData; }

    [DllImport("user32.dll")]
    private static extern bool SetWindowCompositionAttribute(IntPtr hWnd, ref WindowCompositionAttribData data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int MultiByteToWideChar(uint codePage, uint flags,
        byte[] src, int srcLen, char[]? dest, int destLen);

    /// <summary>按 GBK（代码页 936）解码，无需注册 Encoding 提供程序。</summary>
    public static string GbkToString(byte[] bytes)
    {
        int len = MultiByteToWideChar(936, 0x8 /* MB_ERR_INVALID_CHARS */, bytes, bytes.Length, null, 0);
        if (len <= 0) len = MultiByteToWideChar(936, 0, bytes, bytes.Length, null, 0);
        if (len <= 0) return Encoding.UTF8.GetString(bytes);
        var buffer = new char[len];
        MultiByteToWideChar(936, 0, bytes, bytes.Length, buffer, len);
        return new string(buffer);
    }
}
