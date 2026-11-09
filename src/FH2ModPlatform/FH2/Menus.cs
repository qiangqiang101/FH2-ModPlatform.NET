using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using FH2ModPlatform;
using FH2ModPlatform.Interop;
using FH2ModPlatform.Native;

namespace FH2;

/// <summary>
/// The keys the controller sends the menus (the game's WPF Key values): what <see cref="Menus.SendKey"/> and
/// <see cref="Menus.PressPadButton"/> press.
/// </summary>
public enum MenuKey
{
    Pause = 7, View = 13, A = 18, LB = 19, RB = 20, Left = 23, Up = 24, Right = 25, Down = 26, B = 45, X = 67, Y = 68,
    LT = 116, RT = 117, LeftStick = 118, RightStick = 119,
}

/// <summary>
/// The game's menus: a frame of yours on the UI thread (<see cref="Frame"/>), what has the focus, the active
/// window's elements, and the controller's keys pressed for you. Everything here is for the UI thread, inside
/// <see cref="Frame"/>.
/// </summary>
/// <remarks>
/// The game's UI library still has WPF's mouse code, but the Xbox build never feeds it. A mouse mod finds what's
/// under the cursor (<see cref="UiElement.TryGetBounds"/>), focuses it (<see cref="UiElement.Focus"/>) and presses
/// the controller's keys (<see cref="Tap"/>): pages then behave exactly as with a controller.
/// </remarks>
public static unsafe class Menus
{
    private static readonly object s_lock = new();
    private static readonly List<Action<float>> s_frame = new();
    private static volatile bool s_started, s_ready, s_broken;
    private static void** s_original;
    private static uint s_thread;
    private static long s_lastTicks;
    private static float s_dt = 1f / 30;
    private static readonly List<(MenuKey Key, bool Up, float Wait)> s_keys = new();

    internal static nint Base;

    /// <summary>True once the menus' code has been checked and the frame hook is in.</summary>
    public static bool IsReady
    {
        get
        {
            Start();
            return s_ready;
        }
    }

    /// <summary>
    /// Once a UI frame, on the UI thread, before the game's UI updates: the place for everything in this class.
    /// Given the frame's length in seconds (1/1000..1/10). Runs on the title screen and in every menu, and while
    /// driving. Keep it short.
    /// </summary>
    public static event Action<float> Frame
    {
        add
        {
            Start();
            lock (s_lock) s_frame.Add(value);
        }
        remove
        {
            lock (s_lock) s_frame.Remove(value);
        }
    }

    /// <summary>True on the UI thread.</summary>
    public static bool IsUiThread => s_thread != 0 && GetCurrentThreadId() == s_thread;

    /// <summary>True if the UI has run a frame within the last second (the game is up and not minimised).</summary>
    public static bool IsActive => s_ready && Environment.TickCount64 - Interlocked.Read(ref s_lastTicks) < 1000;

    /// <summary>The last UI frame's length, in seconds.</summary>
    public static float FrameTime => s_dt;

    internal static void RequireUiThread(string what)
    {
        if (!IsUiThread) throw new InvalidOperationException($"{what} is for the UI thread: use it in Menus.Frame.");
    }

    /// <summary>The UI's size in its own pixels (the game's render size; 1920 x 1080 at 1080p).</summary>
    public static Vector2 Size
    {
        get
        {
            var app = Base == 0 ? 0 : *(nint*)(Base + Addresses.Application);
            float w = app != 0 ? *(float*)(app + 0x50) : 0, h = app != 0 ? *(float*)(app + 0x54) : 0;
            return new Vector2(w > 0 ? w : 1920, h > 0 ? h : 1080);
        }
    }

    /// <summary>A point in the game window's pixels (<see cref="GameWindow.Cursor"/>) in the UI's.</summary>
    public static Vector2 ToUi(Vector2 window)
    {
        var client = GameWindow.ClientSize;
        if (client.X <= 0 || client.Y <= 0) return window;
        return window * Size / client;
    }

    /// <summary>The active window (the page on screen, or the HUD's while driving), or null.</summary>
    public static UiElement? ActiveWindow
    {
        get
        {
            RequireUiThread("Menus.ActiveWindow");
            var wm = WindowManager;
            return wm == 0 ? null : UiElement.Of(*(nint*)(wm + Addresses.WmActiveWindow));
        }
    }

    /// <summary>The element with the keyboard focus (the page itself, a button, a list), or null.</summary>
    public static UiElement? Focused
    {
        get
        {
            RequireUiThread("Menus.Focused");
            var iface = ((delegate* unmanaged<nint>)(Base + Addresses.FocusedElement.Offset))();
            if (iface == 0) return null;
            return UiElement.Of(((delegate* unmanaged<nint, nint>)(*(nint*)*(nint*)iface))(iface));
        }
    }

    /// <summary>The focused element's class name ("" for none): "UIWindow" while driving, "MapScene" on the map...</summary>
    public static string FocusedTypeName => Focused?.TypeName ?? "";

    /// <summary>Sends a key to the menus now, as the controller sends it (down, or up).</summary>
    public static void SendKey(MenuKey key, bool up)
    {
        RequireUiThread("Menus.SendKey");
        var wm = WindowManager;
        if (wm == 0) return;
        int code = ((delegate* unmanaged<int, int>)(Base + Addresses.KeyCode.Offset))((int)key);
        var fn = (delegate* unmanaged<nint, byte, int, int, byte, byte, byte>)(*(nint*)(*(nint*)wm + Addresses.WmKeySlot));
        fn(wm, up ? (byte)1 : (byte)0, code, 0, 0, 0);
    }

    /// <summary>
    /// Sends a key in a later frame, one key event a frame (as the pad's come: menus that look at what's held
    /// each frame miss a press and release in one), after waiting <paramref name="wait"/> seconds.
    /// </summary>
    public static void QueueKey(MenuKey key, bool up, float wait = 0)
    {
        lock (s_keys)
            if (s_keys.Count < 64) s_keys.Add((key, up, wait));
    }

    /// <summary>A press and release of <paramref name="key"/> (<see cref="QueueKey"/>), after <paramref name="wait"/> seconds.</summary>
    public static void Tap(MenuKey key, float wait = 0)
    {
        QueueKey(key, false, wait);
        QueueKey(key, true);
    }

    /// <summary>How many queued key events are still to go.</summary>
    public static int QueuedKeys
    {
        get
        {
            lock (s_keys) return s_keys.Count;
        }
    }

    /// <summary>
    /// Presses the controller button that sends <paramref name="key"/> to the menus, as the pad does: pages that
    /// ask about the button itself see it too (the pause menu's Y). Held at least 80 ms, and with
    /// <paramref name="hold"/> until <see cref="ReleasePadButton"/>. False if the input hooks are off or a press
    /// is under way; then <see cref="Tap"/> is the way.
    /// </summary>
    public static bool PressPadButton(MenuKey key, bool hold = false) => GameInput.PressMenuPad((int)key, hold);

    /// <summary>Lets go of the button <see cref="PressPadButton"/> held.</summary>
    public static void ReleasePadButton() => GameInput.ReleaseMenuPad();

    private static nint WindowManager => ((delegate* unmanaged<nint>)(Base + Addresses.GetWindowManager.Offset))();

    // ---- Starting --------------------------------------------------------------------------------

    private static void Start()
    {
        if (s_started) return;
        lock (s_lock)
        {
            if (s_started) return;
            s_started = true;
        }
        new Thread(Install) { IsBackground = true, Name = "FH2.Menus start" }.Start();
    }

    private static void Install()
    {
        var owner = ModPlatform.GameOwner;
        try
        {
            var b = ModPlatform.WaitForCode();
            if (b == 0) return;
            if (Game.Mismatch(b, Addresses.MenusCore) is { } bad)
            {
                Game.Log(owner, $"Menus off: the game's code isn't as expected at {bad}");
                return;
            }
            Base = b;
            s_original = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
            using var u = new Utf8Scope();
            var hook = ModPlatform.Api->HookPointer(owner, u.Str("FH2 menus (UserInterface::Update)"), (void**)(b + Addresses.UiUpdateSlot),
                (delegate* unmanaged<nint, float, void>)&OnUpdate, s_original);
            if (hook is null || ModPlatform.Api->EnableHooks(owner, hook) == 0)
            {
                Game.Log(owner, "Menus off: couldn't hook the UI's update");
                return;
            }
            s_ready = true;
            GameInput.Install(b, owner);
        }
        catch (Exception e)
        {
            Game.Log(owner, "Menus off: " + e);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnUpdate(nint self, float dt)
    {
        try
        {
            s_thread = GetCurrentThreadId();
            long now = Stopwatch.GetTimestamp();
            long last = Interlocked.Exchange(ref s_lastTicks, Environment.TickCount64);
            s_dt = s_lastStamp == 0 ? 1f / 30 : Math.Clamp((float)((now - s_lastStamp) / (double)Stopwatch.Frequency), 0.001f, 0.1f);
            s_lastStamp = now;
            if (!s_broken)
            {
                GameInput.MenuPadFrame();
                Action<float>[] handlers;
                lock (s_lock) handlers = s_frame.ToArray();
                foreach (var h in handlers)
                {
                    try { h(s_dt); }
                    catch (Exception e)
                    {
                        Game.Log(ModPlatform.GameOwner, $"Menus.Frame handler {h.Method.DeclaringType?.FullName}.{h.Method.Name} failed: {e}");
                    }
                }
                FlushOneKey();
                ButtonIcons.UiFrame();
                GameInput.BeforeUiUpdate();
            }
        }
        catch (Exception e)
        {
            s_broken = true;
            Game.Log(ModPlatform.GameOwner, "Menus stopped: " + e.Message);
        }
        ((delegate* unmanaged<nint, float, void>)*s_original)(self, dt);
    }

    private static long s_lastStamp;

    private static void FlushOneKey()
    {
        (MenuKey Key, bool Up, float Wait) k;
        lock (s_keys)
        {
            if (s_keys.Count == 0) return;
            k = s_keys[0];
            if (k.Wait > 0)
            {
                s_keys[0] = (k.Key, k.Up, k.Wait - s_dt);
                return;
            }
            s_keys.RemoveAt(0);
        }
        SendKey(k.Key, k.Up);
    }

    /// <summary>A hosted mod is being replaced: its frame handlers and queued keys go.</summary>
    internal static void Forget(System.Reflection.Assembly assembly)
    {
        lock (s_lock) s_frame.RemoveAll(h => h.Method.DeclaringType?.Assembly == assembly);
        lock (s_keys) s_keys.Clear();
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
