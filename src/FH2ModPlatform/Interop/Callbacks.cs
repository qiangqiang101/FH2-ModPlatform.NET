using System.Runtime.InteropServices;
using FH2ModPlatform.Native;

namespace FH2ModPlatform.Interop;

/// <summary>
/// The native-callable trampolines behind every callback. The platform's
/// <c>void* user</c> is a GCHandle to a <see cref="Box"/>. An exception must
/// never unwind into the game, so each one is caught and logged to its mod.
/// </summary>
/// <remarks>
/// A box outlives the mod that made it: when a hosted mod is reloaded, the
/// platform keeps calling the same box, and the box is pointed at the new
/// copy's delegate (or at nothing, if the new copy no longer adds it).
/// </remarks>
internal static unsafe class Callbacks
{
    internal sealed class Box(Mod mod, object value)
    {
        public volatile Mod Mod = mod;
        public volatile object? Value = value;
    }

    private static Box Get(void* user) => (Box)GCHandle.FromIntPtr((nint)user).Target!;

    private static void Report(Mod mod, string what, Exception e)
    {
        try { mod.LogException(what, e); } catch { }
    }

    [UnmanagedCallersOnly]
    internal static void SettingChanged(void* user, byte* key, int value)
    {
        var b = Get(user);
        if (b.Value is not Action<int> fn) return;
        try { fn(value); }
        catch (Exception e) { Report(b.Mod, $"setting {NativeString.FromUtf8(key)} change handler", e); }
    }

    [UnmanagedCallersOnly]
    internal static void SliderChanged(void* user, byte* key, float value)
    {
        var b = Get(user);
        if (b.Value is not Action<float> fn) return;
        try { fn(value); }
        catch (Exception e) { Report(b.Mod, $"slider {NativeString.FromUtf8(key)} change handler", e); }
    }

    [UnmanagedCallersOnly]
    internal static void Instruction(FH2Context* ctx, void* user)
    {
        var b = Get(user);
        if (b.Value is not InstructionHandler fn) return;
        try { fn(ref *ctx); }
        catch (Exception e) { Report(b.Mod, "instruction hook", e); }
    }

    [UnmanagedCallersOnly]
    internal static void PageOpened(void* user, byte* pageKey)
    {
        var b = Get(user);
        if (b.Value is not PageOptions p) return;
        try { p.Opened?.Invoke(); }
        catch (Exception e) { Report(b.Mod, $"page {p.Key} open handler", e); }
    }

    [UnmanagedCallersOnly]
    internal static void PageSelected(void* user, byte* pageKey, byte* rowKey)
    {
        var b = Get(user);
        if (b.Value is not PageOptions p) return;
        try { p.Selected?.Invoke(NativeString.FromUtf8(rowKey) ?? ""); }
        catch (Exception e) { Report(b.Mod, $"page {p.Key} select handler", e); }
    }

    [UnmanagedCallersOnly]
    internal static int PageButton(void* user, byte* pageKey, int button)
    {
        var b = Get(user);
        if (b.Value is not PageOptions p) return 0;
        try { return (int)(p.ButtonPressed?.Invoke((PadButton)button) ?? PageButtonResult.Ignored); }
        catch (Exception e) { Report(b.Mod, $"page {p.Key} button handler", e); return 0; }
    }

    [UnmanagedCallersOnly]
    internal static void PageSlot(void* user, byte* pageKey, int slot, int value)
    {
        var b = Get(user);
        if (b.Value is not PageOptions p) return;
        try { p.SlotChanged?.Invoke(slot, value); }
        catch (Exception e) { Report(b.Mod, $"page {p.Key} slot handler", e); }
    }

    [UnmanagedCallersOnly]
    internal static int TitlePressed(void* user, byte* key)
    {
        var b = Get(user);
        if (b.Value is not TitleEntryOptions t) return 0;
        try { return (int)(t.Pressed?.Invoke() ?? TitleResult.Handled); }
        catch (Exception e) { Report(b.Mod, $"title entry {t.Key}", e); return 0; }
    }

    [UnmanagedCallersOnly]
    internal static int TitleChosen(void* user, byte* key, uint choice)
    {
        var b = Get(user);
        if (b.Value is not TitleEntryOptions t) return 0;
        try { return (int)(t.Chosen?.Invoke((int)choice) ?? TitleResult.Handled); }
        catch (Exception e) { Report(b.Mod, $"title entry {t.Key}", e); return 0; }
    }

    [UnmanagedCallersOnly]
    internal static void HotkeyPressed(void* user, byte* key)
    {
        var b = Get(user);
        if (b.Value is not HotkeyOptions h) return;
        try { h.Pressed?.Invoke(); }
        catch (Exception e) { Report(b.Mod, $"hotkey {h.Key}", e); }
    }

    [UnmanagedCallersOnly]
    internal static void HotkeyRebound(void* user, byte* key, uint vk, uint modifiers, uint chord)
    {
        var b = Get(user);
        if (b.Value is not HotkeyOptions h) return;
        try { h.Rebound?.Invoke(new HotkeyBinding(vk, (KeyModifiers)modifiers, (PadChord)chord)); }
        catch (Exception e) { Report(b.Mod, $"hotkey {h.Key} rebind handler", e); }
    }

    [UnmanagedCallersOnly]
    internal static void Command(void* user, byte* name, byte* args, byte* reply, nuint replySize)
    {
        var b = Get(user);
        string answer;
        if (b.Value is not Func<string, string> fn)
        {
            answer = $"error: {b.Mod.Info.Name} no longer has this command (it was reloaded without it)";
        }
        else
        {
            try { answer = fn(NativeString.FromUtf8(args) ?? ""); }
            catch (Exception e)
            {
                Report(b.Mod, $"command {NativeString.FromUtf8(name)}", e);
                answer = "error: " + e.Message;
            }
        }
        NativeString.WriteUtf8(answer ?? "", reply, replySize);
    }
}
