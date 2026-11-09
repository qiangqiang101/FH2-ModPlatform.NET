using System.Runtime.InteropServices;
using FH2ModPlatform;
using FH2ModPlatform.Interop;
using FH2ModPlatform.Native;

namespace FH2;

/// <summary>Where a controller button's picture is shown.</summary>
public enum ButtonIconPlace
{
    /// <summary>The help bar ("(A) Select (B) Back"): the picture may be wider than high (up to 4x).</summary>
    HelpBar,
    /// <summary>The HUD's prompts while driving ("(X) Enter garage"): a square.</summary>
    HudPrompt,
    /// <summary>A picture a page names itself ("PRESS (A) TO START", the tab strips' LB/RB): a square.</summary>
    Page,
}

/// <summary>A picture asked for: the button ("A", "X", "Menu", "View", "LB", "LT", "L3", "DPadUp", "DPadUpDown"...),
/// large (the HUD's, "_Large") or not, and where.</summary>
public readonly record struct ButtonIconRequest(string Button, bool Large, ButtonIconPlace Place);

/// <summary>
/// A picture to show instead: a texture you've written under ui/textures/data_bound (with <see cref="Picture.ToSwatchbin"/>
/// and <see cref="FH2ModPlatform.Mod.SetFile(string, byte[])"/>), named without the extension ("MyKeys/Key_0D"), and how many times as
/// wide as high it is (help bar only).
/// </summary>
public readonly record struct ButtonIcon(string Texture, float Aspect = 1);

/// <summary>
/// The controller's button pictures in the menus and on the HUD, shown as something else (a keyboard mod's keys):
/// <see cref="Enable"/> with what to show for each button, <see cref="Refresh"/> when that changes. UI thread for
/// the pictures; Enable and Refresh from anywhere.
/// </summary>
public static unsafe class ButtonIcons
{
    private const string Prefix = "A_BitmapImage_ControllerButton_";
    private static Func<ButtonIconRequest, ButtonIcon?>? s_pick;
    private static volatile bool s_hooked, s_shared, s_widths, s_stale;
    private static void** s_original;
    private static readonly Dictionary<string, nint> s_images = new();          // our BitmapImages, made once
    private static readonly Dictionary<string, nint> s_uris = new();            // their Uris
    private static readonly List<nint> s_activate = new();                      // HUD prompts that showed a picture (references of ours)
    private static readonly List<(string Button, bool Large, nint Image, nint PadUri)> s_sharedIcons = new();
    private static bool s_sharedFound;
    private static readonly List<ResourceOverride> s_overrides = new();

    private sealed class ResourceOverride
    {
        public required string ControlType, Resource;
        public required Func<ButtonIcon?> Pick;
        public nint Control, Image, PadUri;
        public long Tried;
    }

    /// <summary>True once the pictures can be swapped (the game's code is as expected).</summary>
    public static bool IsReady => s_hooked;

    /// <summary>
    /// From now on, <paramref name="pick"/> says what each button shows (null: the controller's own), asked each time
    /// the game shows one. Call <see cref="Refresh"/> when its answers change.
    /// </summary>
    public static void Enable(Func<ButtonIconRequest, ButtonIcon?> pick)
    {
        s_pick = pick;
        if (!Menus.IsReady)
            new Thread(() =>
            {
                for (int i = 0; i < 600 && !Menus.IsReady; i++) Thread.Sleep(100);
                Install();
            }) { IsBackground = true }.Start();
        else Install();
        s_stale = true;
    }

    /// <summary>Shows the pictures on screen again, asking <see cref="Enable"/>'s pick anew (the next UI frame).</summary>
    public static void Refresh() => s_stale = true;

    /// <summary>
    /// A picture another control keeps as a resource of its own (FH2 ANNA's HUD: control "HUDAnnaControl", resource
    /// "FH2ANNA_Icon_Open"): shown as <paramref name="pick"/> says (null: its own), again at each <see cref="Refresh"/>.
    /// </summary>
    public static void Override(string controlType, string resource, Func<ButtonIcon?> pick)
    {
        lock (s_overrides) s_overrides.Add(new ResourceOverride { ControlType = controlType, Resource = resource, Pick = pick });
        s_stale = true;
    }

    private static readonly object s_installLock = new();
    private static bool s_installed;

    private static void Install()
    {
        lock (s_installLock)
        {
            if (s_installed) return;
            s_installed = true;
        }
        var b = Menus.Base;
        var owner = ModPlatform.GameOwner;
        if (b == 0) return;
        bool CallsFind(int site) => *(byte*)(b + site) == 0xE8 && b + site + 5 + *(int*)(b + site + 1) == b + Addresses.FindResource.Offset;
        if (Game.Mismatch(b, [Addresses.IconUpdate, Addresses.MakeAvuiString, Addresses.FindResource, Addresses.BitmapCanConvert, Addresses.BitmapConvertFrom]) is { } bad ||
            !Addresses.IconFindCalls.All(CallsFind))
        {
            Game.Log(owner, "ButtonIcons off: the help bar's code isn't as expected");
            return;
        }
        s_original = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
        using var u = new Utf8Scope();
        foreach (var site in Addresses.IconFindCalls)
        {
            var hook = ModPlatform.Api->HookCall(owner, u.Str("FH2 button icons: help bar"), (void*)(b + site), (delegate* unmanaged<nint, nint, nint>)&OnHelpFind, s_original);
            if (hook is null || ModPlatform.Api->EnableHooks(owner, hook) == 0)
            {
                Game.Log(owner, "ButtonIcons off: couldn't hook the help bar");
                return;
            }
        }
        if (Game.Mismatch(b, [Addresses.ActivateUpdate]) is null && CallsFind(Addresses.ActivateFindCall))
        {
            var slot = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
            var hook = ModPlatform.Api->HookCall(owner, u.Str("FH2 button icons: HUD prompts"), (void*)(b + Addresses.ActivateFindCall),
                (delegate* unmanaged<nint, nint, nint>)&OnActivateFind, slot);
            if (hook is null || ModPlatform.Api->EnableHooks(owner, hook) == 0) Game.Log(owner, "ButtonIcons: the HUD's prompts keep the controller's buttons");
        }
        s_shared = Game.Mismatch(b, [Addresses.DpSetValue]) is null;
        s_widths = s_shared && Game.Mismatch(b, [Addresses.BoxFloat]) is null;
        s_hooked = true;
    }

    private static nint FindOriginal(nint element, nint name) =>
        ((delegate* unmanaged<nint, nint, nint>)(Menus.Base + Addresses.FindResource.Offset))(element, name);

    // "A_BitmapImage_ControllerButton_X_Large" -> ("X", large)
    private static bool Parse(nint name, out string button, out bool large)
    {
        button = "";
        large = false;
        if (name == 0) return false;
        var text = *(char**)(name + 0x28);
        int n = *(int*)(name + 0x30);
        if (text is null || n <= Prefix.Length || n >= 128) return false;
        var s = new string(text, 0, n);
        if (!s.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        button = s[Prefix.Length..];
        foreach (var size in (string[])["_Large", "_Medium", "_Small"])
            if (button.EndsWith(size, StringComparison.Ordinal))
            {
                large = size == "_Large";
                button = button[..^size.Length];
            }
        return true;
    }

    private static ButtonIcon? Pick(string button, bool large, ButtonIconPlace place)
    {
        try { return s_pick?.Invoke(new ButtonIconRequest(button, large, place)); }
        catch (Exception e)
        {
            Game.Log(ModPlatform.GameOwner, "ButtonIcons pick: " + e.Message);
            return null;
        }
    }

    [UnmanagedCallersOnly]
    private static nint OnHelpFind(nint element, nint name)
    {
        try
        {
            if (s_pick is not null && Parse(name, out var button, out var large) && Pick(button, large, ButtonIconPlace.HelpBar) is { } icon &&
                Image(icon.Texture) is var image and not 0)
            {
                if (element != 0) SetIconWidth(element, icon.Aspect);
                return image;
            }
            if (element != 0 && s_pick is not null) SetIconWidth(element, 1);   // the pad's own again: square
        }
        catch (Exception e)
        {
            Game.Log(ModPlatform.GameOwner, "ButtonIcons: " + e.Message);
        }
        return FindOriginal(element, name);
    }

    [UnmanagedCallersOnly]
    private static nint OnActivateFind(nint element, nint name)
    {
        try
        {
            if (element != 0 && !s_activate.Contains(element))
            {
                UiElement.AddRef(element);
                s_activate.Add(element);
                if (s_activate.Count > 16)
                {
                    UiElement.Release(s_activate[0]);
                    s_activate.RemoveAt(0);
                }
            }
            if (s_pick is not null && Parse(name, out var button, out var large) && Pick(button, large, ButtonIconPlace.HudPrompt) is { } icon &&
                Image(icon.Texture) is var image and not 0)
                return image;
        }
        catch (Exception e)
        {
            Game.Log(ModPlatform.GameOwner, "ButtonIcons: " + e.Message);
        }
        return FindOriginal(element, name);
    }

    // A BitmapImage of a texture of ours, made once (and kept for good) as XAML makes one from a path.
    private static nint Image(string texture)
    {
        if (s_images.TryGetValue(texture, out var made)) return made;
        var text = AvuiString("/ForzaUI;component/Textures/Data_Bound/" + texture + ".png");
        nint converter = Menus.Base + Addresses.BitmapConverterVtable;   // an object, as far as it can tell: its vtable
        nint image = 0;
        ((delegate* unmanaged<nint*, nint*, nint, nint, nint>)(*(nint*)(converter + 8)))(&converter, &image, 0, text);
        s_images[texture] = image;
        return image;
    }

    // An AVUI String (kept for good: the game may hold on to what it's asked with).
    internal static nint AvuiString(string s)
    {
        var text = (nint)NativeMemory.AllocZeroed(0x80);
        var bytes = System.Text.Encoding.UTF8.GetBytes(s + "\0");
        fixed (byte* p = bytes)
            ((delegate* unmanaged<nint, byte*, int, nint>)(Menus.Base + Addresses.MakeAvuiString.Offset))(text, p, -1);
        UiElement.AddRef(text);
        return text;
    }

    private static nint UriOf(nint image)
    {
        if (image == 0) return 0;
        var boxed = new UiElement(image).GetValue(Addresses.UriSourceDp);
        if (boxed == UiElement.Unset)
        {
            UiElement.Release(boxed);
            return 0;
        }
        return boxed;
    }

    private static void SetValue(nint obj, int dpRva, nint value) =>
        ((delegate* unmanaged<nint, nint, nint, void>)(Menus.Base + Addresses.DpSetValue.Offset))(obj, *(nint*)(Menus.Base + dpRva), value);

    // A wide picture in the help bar: its icon and the icon's shadow, square in the template, made as wide as it is.
    private static void SetIconWidth(nint control, float aspect)
    {
        if (!s_widths || *(nint*)(Menus.Base + Addresses.WidthDp) == 0) return;
        foreach (var off in (int[])[Addresses.HelpIcon, Addresses.HelpIconShadow])
        {
            var icon = *(nint*)(control + off);
            if (icon == 0) continue;
            var boxed = new UiElement(icon).GetValue(Addresses.HeightDp);
            float h = boxed != 0 && boxed != UiElement.Unset ? *(float*)(boxed + Addresses.BoxedValue) : 0;
            UiElement.Release(boxed);
            if (!(h > 0 && h < 1000)) continue;
            float w = h * aspect;
            nint value = 0;
            ((delegate* unmanaged<nint*, float*, nint*>)(Menus.Base + Addresses.BoxFloat.Offset))(&value, &w);
            if (value == 0) continue;
            SetValue(icon, Addresses.WidthDp, value);
            UiElement.Release(value);
        }
    }

    // ---- Each UI frame: the pictures shown again when they're stale ----------------------------------

    internal static void UiFrame()
    {
        if (!s_hooked || s_pick is null) return;
        bool stale = s_stale;
        s_stale = false;
        OverridesFrame(ref stale);
        if (!stale) return;
        if (!SwapShared()) s_stale = true;   // no window to look from yet: again next frame
        if (Menus.ActiveWindow is { } root)
        {
            int visited = 0;
            RefreshHelpBar(root.Handle, 0, ref visited);
        }
        for (int i = 0; i < s_activate.Count;)
        {
            var el = s_activate[i];
            if (*(int*)(el + Addresses.ObjRefCount) <= 1)   // only we hold it: off the screen
            {
                UiElement.Release(el);
                s_activate.RemoveAt(i);
                continue;
            }
            *(int*)(el + Addresses.ActivateShown) = -1;   // else it's the control it shows, and it does nothing
            ((delegate* unmanaged<nint, void>)(Menus.Base + Addresses.ActivateUpdate.Offset))(el);
            i++;
        }
    }

    private static void RefreshHelpBar(nint v, int depth, ref int visited)
    {
        if (v == 0 || depth > 80 || ++visited > 20000 || !UiElement.IsType(v, UiType.Visual)) return;
        if (UiElement.IsType(v, UiType.HelpButton))
        {
            int type = new UiElement(v).HelpButtonType;
            if (type >= 0) ((delegate* unmanaged<nint, int, void>)(Menus.Base + Addresses.IconUpdate.Offset))(v, type);
            return;
        }
        var e = new UiElement(v);
        for (int i = 0, n = e.ChildCount; i < n; i++)
            RefreshHelpBar(((delegate* unmanaged<nint, int, nint>)(*(nint*)(*(nint*)v + Addresses.VtChild)))(v, i), depth + 1, ref visited);
    }

    // Pictures pages name themselves are the game's own BitmapImages, shared by everything showing them: their
    // UriSource is pointed at our texture, and back at the pad's.
    private static bool SwapShared()
    {
        if (!s_shared) return true;
        if (!s_sharedFound)
        {
            if (Menus.ActiveWindow is not { } root) return false;
            foreach (var b in (string[])["A", "B", "X", "Y", "Menu", "View", "LB", "RB", "DPadUp", "DPadDown", "DPadLeft", "DPadRight",
                                         "DPadUpDown", "DPadLeftRight", "LT", "RT", "L3", "R3"])
                foreach (var large in (bool[])[false, true])
                {
                    var image = FindOriginal(root.Handle, AvuiString(Prefix + b + (large ? "_Large" : "")));
                    if (image == 0 || UiElement.RttiName(image) != "BitmapImage@AVUI") continue;
                    var uri = UriOf(image);
                    if (uri == 0) continue;
                    UiElement.AddRef(image);
                    s_sharedIcons.Add((b, large, image, uri));
                }
            s_sharedFound = s_sharedIcons.Count > 0;
            if (!s_sharedFound) return false;
        }
        foreach (var (button, large, image, padUri) in s_sharedIcons)
        {
            var uri = padUri;
            if (Pick(button, large, ButtonIconPlace.Page) is { } icon && TextureUri(icon.Texture) is var ours and not 0) uri = ours;
            SetValue(image, Addresses.UriSourceDp, uri);
        }
        return true;
    }

    private static nint TextureUri(string texture)
    {
        if (!s_uris.TryGetValue(texture, out var uri)) s_uris[texture] = uri = UriOf(Image(texture));
        return uri;
    }

    // Overrides: found from their control (searched for from the top of the active window's tree); a control only
    // we hold is gone (a new HUD), and searched for again every 2 s.
    private static void OverridesFrame(ref bool stale)
    {
        if (!s_shared) return;
        ResourceOverride[] all;
        lock (s_overrides) all = s_overrides.ToArray();
        foreach (var o in all)
        {
            if (o.Control != 0 && *(int*)(o.Control + Addresses.ObjRefCount) <= 1)
            {
                UiElement.Release(o.Image); UiElement.Release(o.PadUri); UiElement.Release(o.Control);
                o.Control = o.Image = o.PadUri = 0;
            }
            if (o.Control == 0)
            {
                if (o.Tried != 0 && Environment.TickCount64 - o.Tried < 2000) continue;
                o.Tried = Environment.TickCount64;
                if (!FindOverride(o)) continue;
                stale = true;
            }
        }
        if (!stale) return;
        foreach (var o in all)
        {
            if (o.Image == 0) continue;
            var uri = o.PadUri;
            ButtonIcon? icon = null;
            try { icon = o.Pick(); } catch { }
            if (icon is { } i && TextureUri(i.Texture) is var ours and not 0) uri = ours;
            if (uri != 0) SetValue(o.Image, Addresses.UriSourceDp, uri);
        }
    }

    private static bool FindOverride(ResourceOverride o)
    {
        var root = Menus.ActiveWindow?.Handle ?? 0;
        for (int up = 0; root != 0 && up < 200; up++)
        {
            var parent = *(nint*)(root + Addresses.VisualParent);
            if (parent == 0) break;
            root = parent;
        }
        int visited = 0;
        var control = root == 0 ? 0 : FindByType(root, o.ControlType, 0, ref visited);
        if (control == 0) return false;
        var image = FindOriginal(control, AvuiString(o.Resource));
        if (image != 0 && UiElement.RttiName(image) == "BitmapImage@AVUI" && UriOf(image) is var uri and not 0)
        {
            UiElement.AddRef(image);
            o.Image = image;
            o.PadUri = uri;
        }
        UiElement.AddRef(control);
        o.Control = control;
        return true;
    }

    private static nint FindByType(nint v, string type, int depth, ref int visited)
    {
        if (v == 0 || depth > 80 || ++visited > 30000 || !UiElement.IsType(v, UiType.Visual)) return 0;
        if (UiElement.RttiName(v) == type) return v;
        var e = new UiElement(v);
        for (int i = 0, n = e.ChildCount; i < n; i++)
        {
            var c = ((delegate* unmanaged<nint, int, nint>)(*(nint*)(*(nint*)v + Addresses.VtChild)))(v, i);
            if (FindByType(c, type, depth + 1, ref visited) is var f and not 0) return f;
        }
        return 0;
    }
}
