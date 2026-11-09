using System.Numerics;
using System.Runtime.InteropServices;
using FH2ModPlatform;

namespace FH2;

/// <summary>A mouse button.</summary>
public enum MouseButton { Left, Right, Middle, Back, Forward }

/// <summary>
/// The game's window (Azimuth's): whether it's in front, the cursor over it, the mouse's buttons and wheel, and
/// the keyboard. Any thread. Azimuth keeps the usual mouse messages from the game, so the cursor and buttons are
/// read as they are (<see cref="Cursor"/>, <see cref="IsMouseDown"/>), and the wheel comes from a low-level hook
/// (<see cref="MouseWheel"/>, started the first time it's subscribed to).
/// </summary>
public static unsafe class GameWindow
{
    private static nint s_hwnd;
    private static nint s_origProc;
    private static WndProcFn? s_proc;   // kept alive: the window calls it
    private static Thread? s_watch, s_wheelThread;
    private static readonly object s_lock = new();
    private static Func<bool?>? s_showCursor;
    private static volatile bool s_suppressAlt;
    private static event Action<int>? s_wheel;
    private static HookFn? s_hookProc;
    private static nint s_arrow;

    /// <summary>The game's window, or 0 until it's there.</summary>
    public static nint Handle
    {
        get
        {
            Start();
            return s_hwnd != 0 ? s_hwnd : Find();
        }
    }

    /// <summary>True while the game's window is the one in front (it has the keyboard).</summary>
    public static bool IsForeground
    {
        get
        {
            var h = Handle;
            return h != 0 && GetForegroundWindow() == h;
        }
    }

    /// <summary>The size of the game's picture in the window, in pixels.</summary>
    public static Vector2 ClientSize
    {
        get
        {
            RECT r;
            return Handle != 0 && GetClientRect(Handle, &r) != 0 ? new Vector2(r.Right, r.Bottom) : Vector2.Zero;
        }
    }

    /// <summary>The cursor in the window's pixels (it may be outside).</summary>
    public static Vector2 Cursor
    {
        get
        {
            POINT p;
            if (Handle == 0 || GetCursorPos(&p) == 0 || ScreenToClient(Handle, &p) == 0) return new Vector2(-1, -1);
            return new Vector2(p.X, p.Y);
        }
    }

    /// <summary>True when the window is in front and the cursor is over its picture.</summary>
    public static bool IsCursorInside
    {
        get
        {
            if (!IsForeground) return false;
            var c = Cursor;
            var s = ClientSize;
            return c.X >= 0 && c.Y >= 0 && c.X < s.X && c.Y < s.Y;
        }
    }

    /// <summary>Moves the cursor to a point in the window's pixels.</summary>
    public static void SetCursor(Vector2 client)
    {
        var p = new POINT { X = (int)client.X, Y = (int)client.Y };
        if (Handle != 0 && ClientToScreen(Handle, &p) != 0) SetCursorPos(p.X, p.Y);
    }

    /// <summary>True while the button is held (and the window is in front).</summary>
    public static bool IsMouseDown(MouseButton button) => IsForeground && (GetAsyncKeyState(button switch
    {
        MouseButton.Left => 0x01,
        MouseButton.Right => 0x02,
        MouseButton.Middle => 0x04,
        MouseButton.Back => 0x05,
        _ => 0x06,
    }) & 0x8000) != 0;

    /// <summary>True while the key (a Windows virtual-key code, 0x41 for A) is held, with the window in front.</summary>
    public static bool IsKeyDown(int virtualKey) => virtualKey > 0 && IsForeground && (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>
    /// Whether the cursor shows over the game: asked each time Windows asks the window (on every mouse move, and
    /// when <see cref="UpdateCursor"/> says it may have changed). Answer null to leave it to Azimuth (its own screens,
    /// before the game and over it, show their cursor); no function at all leaves it to Azimuth always.
    /// </summary>
    public static Func<bool?>? ShowCursor
    {
        get => s_showCursor;
        set
        {
            s_showCursor = value;
            Start();
        }
    }

    /// <summary>Asks the window again whether the cursor shows (<see cref="ShowCursor"/>), without the mouse moving.</summary>
    public static void UpdateCursor()
    {
        var h = s_hwnd;
        POINT p;
        if (h != 0 && GetCursorPos(&p) != 0 && WindowFromPoint(p) == h)
            PostMessageW(h, 0x20 /* WM_SETCURSOR */, h, (nint)((0x200 /* WM_MOUSEMOVE */ << 16) | 1 /* HTCLIENT */));
    }

    /// <summary>
    /// True: a tap of Alt doesn't open the window's system menu (which stalls the game), for mods with Alt on a key.
    /// </summary>
    public static bool SuppressAltMenu
    {
        get => s_suppressAlt;
        set
        {
            s_suppressAlt = value;
            Start();
        }
    }

    /// <summary>The mouse's wheel turned over the game (in front): + away from you, 120 a notch. On a thread of its own.</summary>
    public static event Action<int> MouseWheel
    {
        add
        {
            s_wheel += value;
            StartWheel();
        }
        remove => s_wheel -= value;
    }

    // ---- The window: found, and its messages watched for the cursor -----------------------------------

    private static void Start()
    {
        lock (s_lock)
        {
            if (s_watch is not null) return;
            s_arrow = LoadCursorW(0, 32512 /* IDC_ARROW */);
            s_watch = new Thread(Watch) { IsBackground = true, Name = "FH2 game window" };
            s_watch.Start();
        }
    }

    private static nint Find()
    {
        uint pid = (uint)Environment.ProcessId;
        for (nint h = FindWindowExW(0, 0, "Azimuth.Game.Win32", null); h != 0; h = FindWindowExW(0, h, "Azimuth.Game.Win32", null))
        {
            GetWindowThreadProcessId(h, out var p);
            if (p == pid) return h;
        }
        return 0;
    }

    private static void Watch()
    {
        s_proc = WndProc;
        var proc = Marshal.GetFunctionPointerForDelegate(s_proc);
        while (true)
        {
            Thread.Sleep(250);
            var h = Find();
            if (h == 0 || h == s_hwnd) continue;
            var old = s_hwnd;
            if (old != 0 && IsWindow(old) != 0 && s_origProc != 0) SetWindowLongPtrW(old, -4, s_origProc);
            s_origProc = SetWindowLongPtrW(h, -4 /* GWLP_WNDPROC */, proc);
            s_hwnd = h;
            RECT r;
            GetClientRect(h, &r);
            Log($"GameWindow: watching the game window ({r.Right}x{r.Bottom}){(s_origProc == 0 ? ", but couldn't subclass it" : "")}");
        }
    }

    private static nint WndProc(nint h, uint msg, nint w, nint l)
    {
        // Alt alone opens the window's menu, which stalls the game.
        if (msg == 0x112 /* WM_SYSCOMMAND */ && (w & 0xFFF0) == 0xF100 /* SC_KEYMENU */ && l == 0 && s_suppressAlt) return 0;
        if (msg == 0x20 /* WM_SETCURSOR */ && (l & 0xFFFF) == 1 /* HTCLIENT */ && s_showCursor is { } show &&
            Answer(show) is { } visible)
        {
            if (!s_toldCursor && visible)
            {
                s_toldCursor = true;
                Log("GameWindow: the cursor shows");
            }
            SetCursorW(visible ? s_arrow : 0);
            return 1;
        }
        return CallWindowProcW(s_origProc, h, msg, w, l);
    }

    private static bool s_toldCursor;

    private static bool? Answer(Func<bool?> show)
    {
        try { return show(); }
        catch { return null; }
    }

    private static void Log(string line)
    {
        if (ModPlatform.IsAttached) Game.Log(ModPlatform.GameOwner, line);
    }

    // ---- The wheel: a low-level mouse hook, set again every few seconds (Windows drops a slow one) ---------

    private static void StartWheel()
    {
        lock (s_lock)
        {
            if (s_wheelThread is not null) return;
            Start();
            s_wheelThread = new Thread(WheelLoop) { IsBackground = true, Name = "FH2 mouse wheel", Priority = ThreadPriority.Highest };
            s_wheelThread.Start();
        }
    }

    private static void WheelLoop()
    {
        s_hookProc = MouseHook;
        var proc = Marshal.GetFunctionPointerForDelegate(s_hookProc);
        var module = GetModuleHandleW(null);
        nint hook = 0;
        while (true)
        {
            if (hook != 0) UnhookWindowsHookEx(hook);
            hook = SetWindowsHookExW(14 /* WH_MOUSE_LL */, proc, module, 0);
            if (hook == 0) return;
            long until = Environment.TickCount64 + 3000;
            for (long now = Environment.TickCount64; now < until; now = Environment.TickCount64)
            {
                MsgWaitForMultipleObjects(0, null, 0, (uint)(until - now), 0x4FF /* QS_ALLINPUT */);
                MSG m;
                while (PeekMessageW(&m, 0, 0, 0, 1) != 0) DispatchMessageW(&m);
            }
        }
    }

    private static nint MouseHook(int code, nint w, nint l)
    {
        if (code == 0 && w == 0x20A /* WM_MOUSEWHEEL */ && s_hwnd != 0 && GetForegroundWindow() == s_hwnd)
        {
            var delta = (short)(((MSLLHOOKSTRUCT*)l)->MouseData >> 16);
            // Off the hook's thread: Windows wants an answer at once.
            if (s_wheel is { } handlers) ThreadPool.UnsafeQueueUserWorkItem(_ => Raise(handlers, delta), null);
        }
        return CallNextHookEx(0, code, w, l);
    }

    private static void Raise(Action<int> handlers, int delta)
    {
        foreach (Action<int> h in handlers.GetInvocationList())
            try { h(delta); }
            catch { }
    }

    // ---- Win32 -------------------------------------------------------------------------------------

    private delegate nint WndProcFn(nint h, uint msg, nint w, nint l);
    private delegate nint HookFn(int code, nint w, nint l);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public nint Hwnd; public uint Message; public nint W, L; public uint Time; public POINT Pt; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT Pt; public uint MouseData, Flags, Time; public nint Extra; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowExW(nint parent, nint after, string cls, string? title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetClientRect(nint h, RECT* r);
    [DllImport("user32.dll")] private static extern int GetCursorPos(POINT* p);
    [DllImport("user32.dll")] private static extern int SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern int ScreenToClient(nint h, POINT* p);
    [DllImport("user32.dll")] private static extern int ClientToScreen(nint h, POINT* p);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern int PostMessageW(nint h, uint msg, nint w, nint l);
    [DllImport("user32.dll")] private static extern int IsWindow(nint h);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint h, int index, nint value);
    [DllImport("user32.dll")] private static extern nint CallWindowProcW(nint proc, nint h, uint msg, nint w, nint l);
    [DllImport("user32.dll", EntryPoint = "SetCursor")] private static extern nint SetCursorW(nint cursor);
    [DllImport("user32.dll")] private static extern nint LoadCursorW(nint instance, nint name);
    [DllImport("user32.dll")] private static extern nint SetWindowsHookExW(int id, nint proc, nint module, uint thread);
    [DllImport("user32.dll")] private static extern int UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint w, nint l);
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjects(uint count, nint* handles, int all, uint ms, uint mask);
    [DllImport("user32.dll")] private static extern int PeekMessageW(MSG* m, nint h, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(MSG* m);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
}
