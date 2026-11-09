using System.Runtime.InteropServices;

namespace FH2;

/// <summary>
/// Lines of text drawn over the game, for watching your mod's state while you drive: a click-through box
/// in the top left corner of the game window. Needs the game in borderless or windowed mode. Any thread.
/// </summary>
/// <example>
/// <code>
/// DebugOverlay.Visible = True
/// DebugOverlay.Text = $"fuel {fuel:0.0} s{vbLf}boost {boost:0.00}"
/// </code>
/// </example>
public static unsafe class DebugOverlay
{
    private static volatile string s_text = "";
    private static volatile bool s_visible;
    private static Thread? s_thread;
    private static readonly object s_lock = new();
    private static nint s_hwnd;
    private static WndProc? s_proc;   // kept alive: the window calls it

    /// <summary>Shows or hides the box.</summary>
    public static bool Visible
    {
        get => s_visible;
        set
        {
            s_visible = value;
            if (value) Start();
        }
    }

    /// <summary>What it shows: one line per line of text (up to 40).</summary>
    public static string Text
    {
        get => s_text;
        set => s_text = value ?? "";
    }

    /// <summary>The window is gone with a reload of the mod that showed it: hide it.</summary>
    internal static void Forget() => s_visible = false;

    private static void Start()
    {
        lock (s_lock)
        {
            if (s_thread is not null) return;
            s_thread = new Thread(Run) { IsBackground = true, Name = "FH2 debug overlay" };
            s_thread.Start();
        }
    }

    private const int LineHeight = 17, Pad = 6;

    private static void Run()
    {
        s_proc = Proc;
        var instance = GetModuleHandleW(null);
        fixed (char* name = "FH2ModPlatformDebugOverlay")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_proc),
                hInstance = instance,
                lpszClassName = name,
            };
            if (RegisterClassExW(&wc) == 0) return;
            s_hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                name, name, WS_POPUP, 0, 0, 100, 100, 0, 0, instance, null);
        }
        if (s_hwnd == 0) return;
        SetLayeredWindowAttributes(s_hwnd, 0xFF00FF, 0, LWA_COLORKEY);   // magenta: see-through

        bool shown = false;
        while (true)
        {
            MSG m;
            while (PeekMessageW(&m, 0, 0, 0, 1) != 0)
            {
                TranslateMessage(&m);
                DispatchMessageW(&m);
            }
            Thread.Sleep(100);
            if (!s_visible)
            {
                if (shown) ShowWindow(s_hwnd, 0);
                shown = false;
                continue;
            }
            var game = GameWindow();
            if (game == 0) continue;
            POINT origin = default;
            ClientToScreen(game, &origin);
            int lines = Math.Min(40, s_text.Split('\n').Length);
            MoveWindow(s_hwnd, origin.X + 12, origin.Y + 12, 1100, lines * LineHeight + 2 * Pad, 1);
            SetWindowPos(s_hwnd, -1, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            shown = true;
            InvalidateRect(s_hwnd, null, 1);
        }
    }

    // The game's window: this process's biggest visible one.
    private static nint GameWindow()
    {
        nint best = 0;
        long bestArea = 0;
        uint pid = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == s_hwnd || IsWindowVisible(hwnd) == 0) return 1;
            GetWindowThreadProcessId(hwnd, out var p);
            if (p != pid || (GetWindowLongW(hwnd, -20) & WS_EX_TOOLWINDOW) != 0) return 1;
            RECT rc;
            GetClientRect(hwnd, &rc);
            long area = (long)(rc.Right - rc.Left) * (rc.Bottom - rc.Top);
            if (area > bestArea)
            {
                bestArea = area;
                best = hwnd;
            }
            return 1;
        }, 0);
        return best;
    }

    private static nint Proc(nint hwnd, uint msg, nint wp, nint lp)
    {
        switch (msg)
        {
            case 0x14: return 1;    // WM_ERASEBKGND
            case 0x84: return -1;   // WM_NCHITTEST: HTTRANSPARENT
            case 0x0F:              // WM_PAINT
            {
                PAINTSTRUCT ps;
                var dc = BeginPaint(hwnd, &ps);
                RECT rc;
                GetClientRect(hwnd, &rc);
                var key = CreateSolidBrush(0xFF00FF);
                FillRect(dc, &rc, key);
                DeleteObject(key);
                var lines = s_text.Split('\n').Take(40).Select(l => l.TrimEnd('\r')).ToArray();
                var box = new RECT { Right = rc.Right, Bottom = lines.Length * LineHeight + 2 * Pad };
                var bg = CreateSolidBrush(0x0E0E0E);
                FillRect(dc, &box, bg);
                DeleteObject(bg);
                var font = CreateFontW(15, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 3, 0x31, "Consolas");
                var old = SelectObject(dc, font);
                SetBkMode(dc, 1);
                SetTextColor(dc, 0x82FF5A);   // green (BGR)
                for (int i = 0; i < lines.Length; i++)
                    fixed (char* t = lines[i])
                        TextOutW(dc, Pad, Pad - 1 + i * LineHeight, t, lines[i].Length);
                SelectObject(dc, old);
                DeleteObject(font);
                EndPaint(hwnd, &ps);
                return 0;
            }
        }
        return DefWindowProcW(hwnd, msg, wp, lp);
    }

    // ---- Win32 ------------------------------------------------------------------------------------

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    private const uint LWA_COLORKEY = 1, SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

    private delegate nint WndProc(nint hwnd, uint msg, nint wp, nint lp);
    private delegate int EnumProc(nint hwnd, nint lp);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] private struct PAINTSTRUCT { public nint hdc; public int fErase; public RECT rcPaint; public int fRestore, fIncUpdate; public fixed byte rgbReserved[32]; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll")] private static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
    [DllImport("user32.dll")] private static extern nint CreateWindowExW(uint exStyle, char* cls, char* name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, void* param);
    [DllImport("user32.dll")] private static extern int SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern int PeekMessageW(MSG* msg, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern int TranslateMessage(MSG* msg);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(MSG* msg);
    [DllImport("user32.dll")] private static extern int ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern int ClientToScreen(nint hwnd, POINT* p);
    [DllImport("user32.dll")] private static extern int MoveWindow(nint hwnd, int x, int y, int w, int h, int repaint);
    [DllImport("user32.dll")] private static extern int SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern int InvalidateRect(nint hwnd, RECT* r, int erase);
    [DllImport("user32.dll")] private static extern int EnumWindows(EnumProc proc, nint lp);
    [DllImport("user32.dll")] private static extern int IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern int GetClientRect(nint hwnd, RECT* r);
    [DllImport("user32.dll")] private static extern nint BeginPaint(nint hwnd, PAINTSTRUCT* ps);
    [DllImport("user32.dll")] private static extern int EndPaint(nint hwnd, PAINTSTRUCT* ps);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, RECT* r, nint brush);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint msg, nint wp, nint lp);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern int DeleteObject(nint obj);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] private static extern int TextOutW(nint dc, int x, int y, char* text, int length);
}
