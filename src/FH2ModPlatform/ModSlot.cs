using System.Runtime.InteropServices;
using FH2ModPlatform.Interop;
using FH2ModPlatform.Native;

namespace FH2ModPlatform;

/// <summary>
/// What a mod keeps with the platform across reloads: its registered handle,
/// and the callback boxes behind everything it added. The platform can't take
/// a setting, page, title entry, hotkey or command back, so a reloaded copy of
/// the mod re-binds the boxes the old copy added instead of adding them again.
/// </summary>
internal sealed unsafe class ModSlot
{
    private readonly Dictionary<string, (Callbacks.Box Box, nint User)> _boxes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public FH2Mod* Handle;
    /// <summary>How many copies of the mod have run in this slot (1 = no reload yet).</summary>
    public int Generation;
    /// <summary>A copy before this one made hooks: they're off but still the platform's, so "all my hooks" no longer means all of the handle's.</summary>
    public bool HasRetiredHooks;

    public bool IsReload => Generation > 1;

    /// <summary>
    /// The user pointer for the callback <paramref name="id"/> ("setting:Fast"), now pointing at
    /// <paramref name="value"/>. <paramref name="rebound"/>: a copy before this one added it, so the
    /// platform already has it and mustn't be asked again.
    /// </summary>
    public void* Bind(Mod mod, string id, object value, out bool rebound)
    {
        lock (_boxes)
        {
            if (!_seen.Add(id))
            {
                // Added twice by the same copy: give the platform a box of its own, and let it refuse.
                rebound = false;
                return (void*)GCHandle.ToIntPtr(GCHandle.Alloc(new Callbacks.Box(mod, value)));
            }
            if (_boxes.TryGetValue(id, out var e))
            {
                e.Box.Mod = mod;
                e.Box.Value = value;
                rebound = true;
                return (void*)e.User;
            }
            var box = new Callbacks.Box(mod, value);
            var user = GCHandle.ToIntPtr(GCHandle.Alloc(box));
            _boxes[id] = (box, user);
            rebound = false;
            return (void*)user;
        }
    }

    /// <summary>A new copy of the mod is about to run its OnInit.</summary>
    public void BeginGeneration()
    {
        lock (_boxes)
        {
            Generation++;
            _seen.Clear();
        }
    }

    /// <summary>
    /// After the new copy's OnInit: what it didn't add again goes quiet (its callbacks do nothing).
    /// Returns those ids, for the log.
    /// </summary>
    public List<string> EndGeneration()
    {
        var dropped = new List<string>();
        lock (_boxes)
        {
            foreach (var (id, e) in _boxes)
            {
                if (_seen.Contains(id) || e.Box.Value is null) continue;
                e.Box.Value = null;
                dropped.Add(id);
            }
        }
        return dropped;
    }

    /// <summary>The mod is gone for good (its folder was removed): every callback goes quiet.</summary>
    public void Silence()
    {
        lock (_boxes)
            foreach (var e in _boxes.Values) e.Box.Value = null;
    }
}
