using System.Numerics;
using System.Runtime.InteropServices;

namespace FH2;

/// <summary>Kinds of element in the game's menus (AVUI, the game's WPF).</summary>
public enum UiType
{
    Visual, UIElement, ButtonBase, RepeatButton, ListBoxItem, ComboBoxItem, TabItem, TreeViewItem, MenuItem,
    Slider,
    /// <summary>A list or row of items that has a selection (ListBox, the game's ItemsSelector...).</summary>
    Selector,
    /// <summary>The game's SpinnerControl ("&lt; value &gt;").</summary>
    Spinner,
    /// <summary>One of the help bar's buttons ("(A) Select"): the game's ControllerButtonControl.</summary>
    HelpButton,
}

/// <summary>A rectangle in the UI's pixels.</summary>
public readonly record struct UiRect(float Left, float Top, float Right, float Bottom)
{
    public float Width => Right - Left;
    public float Height => Bottom - Top;
    public Vector2 Center => new((Left + Right) / 2, (Top + Bottom) / 2);
    public bool Contains(Vector2 p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;

    /// <summary>How far <paramref name="p"/> is outside it (0 inside), the larger of across and down.</summary>
    public float DistanceTo(Vector2 p) =>
        Math.Max(Math.Max(Math.Max(Left - p.X, 0), p.X - Right), Math.Max(Math.Max(Top - p.Y, 0), p.Y - Bottom));
}

/// <summary>
/// An element of the game's menus: a page, a panel, a button, a list row. Only valid during the UI frame you got
/// it in (<see cref="Menus.Frame"/>), and only there: the UI thread. Compare them with ==.
/// </summary>
public sealed unsafe class UiElement : IEquatable<UiElement>
{
    internal UiElement(nint handle) => Handle = handle;

    /// <summary>The element's address, for code of your own.</summary>
    public nint Handle { get; }

    private static nint B => Menus.Base;

    internal static UiElement? Of(nint p) => p == 0 ? null : new UiElement(p);

    /// <summary>Its class's name as the game has it ("ListBoxItem", "MapScene", "ItemsSelector").</summary>
    public string TypeName => RttiName(Handle);

    /// <summary>True if it's a <paramref name="type"/> (or derives from one).</summary>
    public bool Is(UiType type) => IsType(Handle, type);

    /// <summary>The element it's drawn in, or null.</summary>
    public UiElement? Parent
    {
        get
        {
            var p = *(nint*)(Handle + Addresses.VisualParent);
            return p != 0 && IsType(p, UiType.Visual) ? new UiElement(p) : null;
        }
    }

    /// <summary>How many elements are drawn in it.</summary>
    public int ChildCount => Math.Min(4096, ((delegate* unmanaged<nint, int>)VFn(Handle, Addresses.VtChildCount))(Handle));

    /// <summary>The <paramref name="index"/>th element drawn in it (later ones are drawn over earlier ones), or null.</summary>
    public UiElement? Child(int index)
    {
        var c = ((delegate* unmanaged<nint, int, nint>)VFn(Handle, Addresses.VtChild))(Handle, index);
        return c != 0 && IsType(c, UiType.Visual) ? new UiElement(c) : null;
    }

    public IEnumerable<UiElement> Children
    {
        get
        {
            for (int i = 0, n = ChildCount; i < n; i++)
                if (Child(i) is { } c) yield return c;
        }
    }

    /// <summary>Its opacity, 0..1 (0: hidden).</summary>
    public float Opacity
    {
        get
        {
            if (((*(uint*)(Handle + Addresses.VisualFlags) >> 3) & 1) == 0) return 1;
            var boxed = *(nint*)(B + Addresses.OpacityDefault);
            if (*(byte*)(B + Addresses.OpacityStored) != 0)
            {
                var o = ((delegate* unmanaged<nint, int, nint>)(B + Addresses.StoredValue.Offset))(Handle, *(int*)(B + Addresses.OpacityStored + 4));
                if (o != 0 && o != Unset) boxed = o;
            }
            return boxed != 0 ? *(float*)(boxed + Addresses.BoxedValue) : 1;
        }
    }

    /// <summary>True once it has a place and a size on the page.</summary>
    public bool IsLaidOut
    {
        get
        {
            var flags = *(uint*)(Handle + Addresses.VisualFlags);
            return (((flags >> 5) & 1) != 0 || ((flags >> 6) & 1) != 0) && *(float*)(Handle + Addresses.RenderSize) > 0;
        }
    }

    /// <summary>Its size, as laid out.</summary>
    public Vector2 RenderSize => new(*(float*)(Handle + Addresses.RenderSize), *(float*)(Handle + Addresses.RenderSize + 4));

    /// <summary>True if it can take the focus.</summary>
    public bool IsFocusable => BoolProperty(Addresses.FocusableDp);

    /// <summary>True if it has the focus (a tab row marks its current tab so).</summary>
    public bool IsFocused => BoolProperty(Addresses.IsFocusedDp);

    /// <summary>What it shows (its DataContext): compare it, never follow it. 0 for none.</summary>
    public nint DataContext => ObjectProperty(Addresses.DataContextDp);

    /// <summary>A <see cref="UiType.Selector"/>'s selected item (compare it with an item's <see cref="DataContext"/>), or 0.</summary>
    public nint SelectedItem => ObjectProperty(Addresses.SelectedItemDp);

    /// <summary>
    /// A <see cref="UiType.HelpButton"/>'s button (the game's ControllerButtonType: 0 A, 1 B, 2 X, 3 Y, 4 Menu,
    /// 5 View, 6 LB, 7 RB, 8 LT, 9 RT, 10 L3, 11 R3, 12/13 d-pad both ways, 14-17 d-pad up/down/left/right...),
    /// or -1.
    /// </summary>
    public int HelpButtonType
    {
        get
        {
            var boxed = GetValue(Addresses.HelpButtonDp);
            var type = boxed != 0 && boxed != Unset ? *(int*)(boxed + Addresses.BoxedValue) : -1;
            Release(boxed);
            return type;
        }
    }

    /// <summary>Gives it the focus, as the d-pad would. True if it has it now.</summary>
    public bool Focus()
    {
        var iface = Handle + Addresses.UiElementInput;
        ((delegate* unmanaged<nint, void>)VFn(iface, 3 * 8))(iface);   // the reference Focus takes
        var output = stackalloc nint[4];
        new Span<nint>(output, 4).Clear();
        ((delegate* unmanaged<nint*, nint, void>)(B + Addresses.KeyboardFocus.Offset))(output, iface);
        Release(output[1]);
        return Menus.Focused == this;
    }

    /// <summary>Where it's laid out, in <paramref name="root"/>'s pixels (the screen's, for the active window).</summary>
    public bool TryGetLayoutBounds(UiElement root, out UiRect bounds)
    {
        bounds = default;
        var flags = *(uint*)(Handle + Addresses.VisualFlags);
        if (((flags >> 5) & 1) == 0 && ((flags >> 6) & 1) == 0) return false;
        float w = *(float*)(Handle + Addresses.RenderSize), h = *(float*)(Handle + Addresses.RenderSize + 4);
        if (!(w > 0) || !(h > 0)) return false;
        var matrix = stackalloc float[16 + 4];
        var m = (float*)(((nint)matrix + 15) & ~(nint)15);   // 16-byte aligned
        nint gt = 0;
        bool simple = ((delegate* unmanaged<nint, nint, byte, nint*, float*, byte>)(B + Addresses.TransformToAncestor.Offset))(Handle, root.Handle, 0, &gt, m) != 0;
        bool ok = simple || gt != 0;
        float l = 1e30f, t = 1e30f, r = -1e30f, b = -1e30f;
        var corners = stackalloc float[8] { 0, 0, w, 0, 0, h, w, h };
        var p = stackalloc float[2];
        for (int i = 0; ok && i < 4; i++)
        {
            if (simple) ((delegate* unmanaged<float*, float*, float*, float*>)(B + Addresses.MatrixTransform.Offset))(m, p, corners + 2 * i);
            else ok = ((delegate* unmanaged<nint, float*, float*, byte>)VFn(gt, Addresses.VtGtTransform))(gt, corners + 2 * i, p) != 0;
            l = Math.Min(l, p[0]); t = Math.Min(t, p[1]); r = Math.Max(r, p[0]); b = Math.Max(b, p[1]);
        }
        Release(gt);
        if (ok)
        {
            bounds = new UiRect(l, t, r, b);
            return true;
        }
        // An animated transform on the way up: the layout offsets alone.
        float x = 0, y = 0;
        for (nint e = Handle; e != 0 && e != root.Handle; e = *(nint*)(e + Addresses.VisualParent))
        {
            if (!IsType(e, UiType.Visual)) return false;
            x += *(float*)(e + Addresses.VisualOffset);
            y += *(float*)(e + Addresses.VisualOffset + 4);
        }
        bounds = new UiRect(x, y, x + w, y + h);
        return true;
    }

    /// <summary>
    /// Where it's drawn, in <paramref name="root"/>'s pixels: as laid out, then as the screen tilt shows it (most
    /// menus are drawn tilted 9 degrees back about their bottom edge, in perspective from the middle).
    /// </summary>
    public bool TryGetBounds(UiElement root, out UiRect bounds)
    {
        if (!TryGetLayoutBounds(root, out bounds)) return false;
        if (!TiltPivot(root, out float bottom)) return true;
        Span<Vector2> q = [new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom), new(bounds.Right, bounds.Top)];
        for (int i = 0; i < 4; i++) q[i] = Tilt(bottom, q[i]);
        bounds = new UiRect(Math.Min(q[0].X, q[2].X), Math.Min(q[0].Y, q[3].Y), Math.Max(q[1].X, q[3].X), Math.Max(q[1].Y, q[2].Y));
        return true;
    }

    // The tilt: measured on the pause menu and the tuning page, in 1080p UI pixels.
    private const float TiltDistance = 2184f, TiltSin = 0.15643447f, TiltCos = 0.98768834f;

    private bool TiltPivot(UiElement root, out float bottom)
    {
        bottom = 0;
        var dp = *(nint*)(B + Addresses.ZPanelTransformDp);
        if (dp == 0) return false;
        for (nint e = Handle; e != 0 && e != root.Handle; e = *(nint*)(e + Addresses.VisualParent))
        {
            if (!IsType(e, UiType.UIElement)) break;
            nint boxed = 0;
            ((delegate* unmanaged<nint, nint*, nint, nint>)(B + Addresses.DpGetValue.Offset))(e, &boxed, dp);
            bool tilted = boxed != 0 && boxed != Unset;
            Release(boxed);
            if (!tilted) continue;
            if (!new UiElement(e).TryGetLayoutBounds(root, out var r)) return false;
            bottom = r.Bottom;
            return true;
        }
        return false;
    }

    private static Vector2 Tilt(float bottom, Vector2 p)
    {
        var size = Menus.Size;
        float scale = size.Y / 1080f;
        float h = bottom - p.Y;
        float s = TiltDistance * scale / (TiltDistance * scale + h * TiltSin);
        float y = bottom - h * TiltCos;
        return new Vector2(size.X / 2 + (p.X - size.X / 2) * s, size.Y / 2 + (y - size.Y / 2) * s);
    }

    // ---- AVUI objects ----------------------------------------------------------------------------

    internal static nint Unset => ((delegate* unmanaged<nint>)(B + Addresses.UnsetValue.Offset))();

    private static nint VFn(nint obj, int offset) => *(nint*)(*(nint*)obj + offset);

    internal static nint TypeOf(nint obj)
    {
        var t = *(nint*)(obj + Addresses.ObjType);
        return t != 0 ? t : ((delegate* unmanaged<nint, nint>)VFn(obj, 3 * 8))(obj);
    }

    private static int TypeRva(UiType t) => t switch
    {
        UiType.Visual => Addresses.TypeVisual,
        UiType.UIElement => Addresses.TypeUiElement,
        UiType.ButtonBase => Addresses.TypeButtonBase,
        UiType.RepeatButton => Addresses.TypeRepeatButton,
        UiType.ListBoxItem => Addresses.TypeListBoxItem,
        UiType.ComboBoxItem => Addresses.TypeComboItem,
        UiType.TabItem => Addresses.TypeTabItem,
        UiType.TreeViewItem => Addresses.TypeTreeItem,
        UiType.MenuItem => Addresses.TypeMenuItem,
        UiType.Slider => Addresses.TypeSlider,
        UiType.Selector => Addresses.TypeSelector,
        UiType.Spinner => Addresses.TypeSpinner,
        _ => Addresses.TypeHelpButton,
    };

    internal static bool IsType(nint obj, UiType type)
    {
        if (obj == 0) return false;
        var t = TypeOf(obj);
        return t != 0 && ((delegate* unmanaged<nint, nint, byte>)(B + Addresses.IsTypeOf.Offset))(t, B + TypeRva(type)) != 0;
    }

    internal static string RttiName(nint obj)
    {
        var vt = *(nint*)obj;
        var col = *(nint*)(vt - 8);
        if (col < B || col >= B + Addresses.ImageSize) return "?";
        var name = Marshal.PtrToStringAnsi(B + *(int*)(col + 12) + 0x10) ?? "?";
        if (name.StartsWith(".?AV", StringComparison.Ordinal)) name = name[4..];
        return name.EndsWith("@@", StringComparison.Ordinal) ? name[..^2] : name;
    }

    /// <summary>Lets go of a reference to an AVUI object (freeing it when it was the last).</summary>
    internal static void Release(nint obj)
    {
        if (obj != 0 && Interlocked.Decrement(ref *(int*)(obj + Addresses.ObjRefCount)) == 0)
            ((delegate* unmanaged<nint, void>)(B + Addresses.ObjectFree.Offset))(obj);
    }

    internal static void AddRef(nint obj)
    {
        if (obj != 0) Interlocked.Increment(ref *(int*)(obj + Addresses.ObjRefCount));
    }

    /// <summary>A dependency property's boxed value (a reference of the caller's), or 0.</summary>
    internal nint GetValue(int dpRva)
    {
        var dp = *(nint*)(B + dpRva);
        if (dp == 0) return 0;
        nint boxed = 0;
        ((delegate* unmanaged<nint, nint*, nint, nint>)(B + Addresses.DpGetValue.Offset))(Handle, &boxed, dp);
        return boxed;
    }

    private bool BoolProperty(int dpRva)
    {
        var boxed = GetValue(dpRva);
        bool v = boxed != 0 && boxed != Unset && *(byte*)(boxed + Addresses.BoxedValue) != 0;
        Release(boxed);
        return v;
    }

    private nint ObjectProperty(int dpRva)
    {
        var boxed = GetValue(dpRva);
        var v = boxed == Unset ? 0 : boxed;
        Release(boxed);   // identity only: whoever holds the value keeps it alive
        return v;
    }

    public bool Equals(UiElement? other) => other is not null && other.Handle == Handle;
    public override bool Equals(object? obj) => Equals(obj as UiElement);
    public override int GetHashCode() => Handle.GetHashCode();
    public static bool operator ==(UiElement? a, UiElement? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(UiElement? a, UiElement? b) => !(a == b);
    public override string ToString() => $"{TypeName} (0x{Handle:X})";
}
