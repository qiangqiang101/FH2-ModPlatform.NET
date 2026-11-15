using System.Runtime.InteropServices;
using FH2ModPlatform.Interop;
using FH2ModPlatform.Native;

namespace FH2ModPlatform;

/// <summary>
/// Base class for an FH2 mod written in .NET. Derive from it, give your
/// <see cref="Info"/>, and do your setup in <see cref="OnInit"/>. Name the
/// class in your project's <c>&lt;FH2ModType&gt;</c> and the build does the rest,
/// as a NativeAOT DLL of its own or a mod for FH2DotNet.dll.
/// </summary>
/// <remarks>
/// Every method may be called from any thread once registered. Callbacks you
/// pass in (settings, pages, hotkeys...) stay alive for the life of the process.
/// Exceptions thrown from them are caught and logged, never passed to the game.
/// </remarks>
public abstract unsafe partial class Mod
{
    private readonly List<GCHandle> _roots = new();
    private readonly List<Hook> _hooks = new();
    private readonly CancellationTokenSource _unloading = new();
    private FH2Mod* _handle;
    internal ModSlot Slot { get; private set; } = new();

    /// <summary>Who your mod is. Read once, before <see cref="OnInit"/>.</summary>
    public abstract ModInfo Info { get; }

    /// <summary>
    /// Called on the platform's thread, right after registering. Do your setup and return
    /// quickly: start a thread of your own for anything slow (waiting for the game, network, big files).
    /// Text, files and SQL you add here are applied for you.
    /// </summary>
    /// <remarks>
    /// A hosted mod's OnInit runs again on a new copy of the mod each time it's reloaded
    /// (<c>reload MyMod</c>). Settings, pages, title entries, hotkeys and commands you add again
    /// with the same key keep their place, now calling the new code; ones you add for the first
    /// time on a reload only show from the next launch. See <see cref="IsReload"/>.
    /// </remarks>
    protected internal abstract void OnInit();

    /// <summary>
    /// A hosted mod is about to be replaced by a newly built copy (or removed). Stop your
    /// threads and timers, close files, undo what isn't the platform's. Your hooks are switched
    /// off for you right after; <see cref="Unloading"/> is cancelled just before. Never called
    /// for NativeAOT mods, or when the game closes.
    /// </summary>
    protected internal virtual void OnUnload() { }

    /// <summary>Cancelled when this copy of the mod is unloaded (a reload). Pass it to your threads and waits.</summary>
    public CancellationToken Unloading => _unloading.Token;

    /// <summary>True when this copy is a reload: the game was running before it started.</summary>
    public bool IsReload => Slot.IsReload;

    /// <summary>
    /// False for a mod that can't be swapped for a new copy while the game runs (one that changes the game as it
    /// loads, say). Platform.NET then leaves the running copy alone when asked to reload it (A on the Hosted .NET
    /// mods page, the reload key, <c>reload</c>, auto-reload) and says a restart is needed. True by default.
    /// </summary>
    public virtual bool Reloadable => true;

    /// <summary>
    /// The folder your mod's files are in, for sounds, pictures and data you ship with it: your folder in
    /// fh2-mods-dotnet (a script's: the scripts folder); a NativeAOT mod's: the folder its DLL is in.
    /// </summary>
    public string ModFolder
    {
        get => _folder ??= NativeFolder();
        internal set => _folder = value;
    }
    private string? _folder;

    // A NativeAOT mod: the folder of the DLL this code is compiled into.
    private static string NativeFolder()
    {
        const uint FromAddress = 0x4, UnchangedRefcount = 0x2;
        var buffer = new char[1024];
        if (GetModuleHandleExW(FromAddress | UnchangedRefcount, (nint)(delegate*<string>)&NativeFolder, out var module))
        {
            fixed (char* b = buffer)
            {
                var n = GetModuleFileNameW(module, b, (uint)buffer.Length);
                if (n > 0) return Path.GetDirectoryName(new string(b, 0, (int)n)) ?? AppContext.BaseDirectory;
            }
        }
        return AppContext.BaseDirectory;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetModuleHandleExW(uint flags, nint address, out nint module);

    [DllImport("kernel32.dll")]
    private static extern uint GetModuleFileNameW(nint module, char* name, uint size);

    /// <summary>Your handle with the platform; null until registered.</summary>
    public FH2Mod* Handle => _handle;
    public bool IsRegistered => _handle is not null;

    private static FH2ModApi* Api => ModPlatform.Api;

    private FH2Mod* H => _handle is not null ? _handle : throw new InvalidOperationException("The mod isn't registered with the platform.");

    // ---- Lifecycle --------------------------------------------------------

    internal enum StartResult { Started, HeldOffline, Refused, Failed }

    /// <summary>Takes over a slot from the copy of this mod before it; it registered already.</summary>
    internal StartResult StartReplacing(ModSlot slot)
    {
        Slot = slot;
        _handle = slot.Handle;
        if (_handle is null) return Start();
        return RunInit();
    }

    /// <summary>This copy is being replaced or removed: OnUnload, then its hooks off and its edits dropped.</summary>
    internal void Retire()
    {
        try { _unloading.Cancel(); }
        catch (Exception e) { LogException("an Unloading callback failed", e); }
        try { OnUnload(); }
        catch (Exception e) { LogException("OnUnload failed", e); }
        global::FH2.Game.Forget(GetType().Assembly);
        if (_handle is null) return;
        lock (_hooks)
        {
            if (_hooks.Count > 0)
            {
                Slot.HasRetiredHooks = true;
                foreach (var h in _hooks) Api->DisableHooks(_handle, h.Handle);
            }
        }
        // The new copy adds its own again; what it leaves out should go.
        Api->ClearStrings(_handle, null);
        if (ModPlatform.Supports(ApiLevel.Files)) Api->ClearSql(_handle);
    }

    internal StartResult Start()
    {
        var info = Info;
        if (ModPlatform.IsHosted && info.OfflineOnly && ModPlatform.PlayingOnline)
        {
            // The platform remembers offline-only per DLL, and every hosted mod shares
            // FH2DotNet.dll: registering would switch all of them off next online launch.
            return StartResult.HeldOffline;
        }

        using (var u = new Utf8Scope())
        {
            var ni = new FH2ModInfo
            {
                Size = (uint)sizeof(FH2ModInfo),
                Name = u.Str(info.Name),
                Version = u.Str(info.Version),
                GithubRepo = u.Str(info.GithubRepo),
                PlatformVersion = u.Str(info.PlatformVersion),
                CheckUpdates = info.CheckUpdates && info.GithubRepo is not null ? 1 : 0,
                Description = u.Str(info.Description),
                OfflineOnly = info.OfflineOnly ? 1 : 0,
            };
            _handle = Api->RegisterMod(&ni);
        }
        if (_handle is null)
            return info.OfflineOnly && ModPlatform.PlayingOnline ? StartResult.HeldOffline : StartResult.Refused;
        Slot.Handle = _handle;
        if (!ModPlatform.IsHosted)
        {
            ModPlatform.GameOwner = _handle;   // a NativeAOT mod's own (hosted ones already have the host's)
            FH2.UI.Prepare();                  // before the game builds its HUD (see UI.Prepare)
        }
        Registered?.Invoke();
        return RunInit();
    }

    /// <summary>Registered with the platform, before OnInit.</summary>
    internal event Action? Registered;

    private StartResult RunInit()
    {
        Slot.BeginGeneration();
        var result = StartResult.Started;
        try
        {
            OnInit();
        }
        catch (Exception e)
        {
            LogException("OnInit failed", e);
            result = StartResult.Failed;
        }
        if (Slot.IsReload)
        {
            foreach (var id in Slot.EndGeneration())
                Log($"reloaded without {id}: it stays until the game restarts, and does nothing");
            // The platform only writes edits for you after the first OnInit.
            Api->ApplyStrings(_handle);
            if (ModPlatform.Supports(ApiLevel.Files))
            {
                var h = (nint)_handle;
                ThreadPool.QueueUserWorkItem(_ => Api->ApplyDatabase((FH2Mod*)h)); // slow; for the next launch
            }
        }
        return result;
    }

    internal void* Root(object o) => (void*)GCHandle.ToIntPtr(Keep(GCHandle.Alloc(new Callbacks.Box(this, o))));

    /// <summary>A callback box kept by key across reloads; <paramref name="rebound"/> means don't add it to the platform again.</summary>
    internal void* Bind(string kind, string key, object value, out bool rebound) =>
        Slot.Bind(this, kind + ":" + key, value, out rebound);

    private GCHandle Keep(GCHandle h)
    {
        lock (_roots) _roots.Add(h);
        return h;
    }

    // ---- Logging ----------------------------------------------------------

    /// <summary>Lines to fh2-mods\platform.log, prefixed with your mod's name.</summary>
    public void Log(string message)
    {
        if (_handle is null) return;
        using var u = new Utf8Scope();
        foreach (var line in message.Split('\n'))
            Api->Log(_handle, u.Str(line.TrimEnd('\r')));
    }

    public void LogException(string what, Exception e) => Log($"{what}: {e}");

    // ---- String tables ----------------------------------------------------

    /// <summary>Sets the string with <paramref name="key"/> in <paramref name="table"/> ("Main.str"), in every language.</summary>
    public void SetString(string table, ushort key, string text)
    {
        using var u = new Utf8Scope();
        fixed (char* t = text) Api->SetString(H, u.Str(table), key, t);
    }

    /// <summary>Replaces every string in <paramref name="table"/> that is exactly <paramref name="from"/>.</summary>
    public void ReplaceString(string table, string from, string to)
    {
        using var u = new Utf8Scope();
        fixed (char* f = from)
        fixed (char* t = to)
            Api->ReplaceString(H, u.Str(table), f, t);
    }

    /// <summary>Forgets your edits to <paramref name="table"/>, or to every table if null.</summary>
    public void ClearStrings(string? table = null)
    {
        using var u = new Utf8Scope();
        Api->ClearStrings(H, u.Str(table));
    }

    /// <summary>Writes changed tables now. Done for you after OnInit; the game reads them once at startup.</summary>
    public void ApplyStrings() => Api->ApplyStrings(H);

    /// <summary>Lines under "PRESS (A) TO START" (keep each under ~40 characters); null or "" clears.</summary>
    public void SetNotice(string? lines)
    {
        using var u = new Utf8Scope();
        Api->SetNotice(H, u.Str(lines));
    }

    // ---- Game files and database (API 2) ----------------------------------

    /// <summary>
    /// Replaces a file the game reads from game:\media ("UI/somefile.xml" for one inside UI.zip).
    /// False if another mod already replaces it or the write failed.
    /// </summary>
    public bool SetFile(string mediaPath, ReadOnlySpan<byte> data)
    {
        ModPlatform.Require(ApiLevel.Files, nameof(SetFile));
        using var u = new Utf8Scope();
        fixed (byte* d = data) return Api->SetFile(H, u.Str(mediaPath), d, (nuint)data.Length) != 0;
    }

    /// <summary>Stops replacing a file, or all of yours if null.</summary>
    public void ClearFile(string? mediaPath = null)
    {
        ModPlatform.Require(ApiLevel.Files, nameof(ClearFile));
        using var u = new Utf8Scope();
        Api->ClearFile(H, u.Str(mediaPath));
    }

    /// <summary>Queues SQL against gamedbRC.slt. Statements added in OnInit are applied for you.</summary>
    public void AddSql(string sql)
    {
        ModPlatform.Require(ApiLevel.Files, nameof(AddSql));
        using var u = new Utf8Scope();
        Api->AddSql(H, u.Str(sql));
    }

    public void ClearSql()
    {
        ModPlatform.Require(ApiLevel.Files, nameof(ClearSql));
        Api->ClearSql(H);
    }

    /// <summary>Rebuilds the database copy now if anything changed. Slow (seconds): call it from your own thread.</summary>
    public void ApplyDatabase()
    {
        ModPlatform.Require(ApiLevel.Files, nameof(ApplyDatabase));
        Api->ApplyDatabase(H);
    }

    // ---- Other mods (API 2) -----------------------------------------------

    /// <summary>
    /// Offers <paramref name="iface"/>, a struct of function pointers that must stay valid forever
    /// (allocate it with NativeMemory), to other mods as <paramref name="name"/>.
    /// </summary>
    public void PublishInterface(string name, uint version, void* iface)
    {
        ModPlatform.Require(ApiLevel.Files, nameof(PublishInterface));
        using var u = new Utf8Scope();
        Api->PublishInterface(H, u.Str(name), version, iface);
    }

    /// <summary>Publishes a copy of <paramref name="iface"/> in native memory that's never freed.</summary>
    public T* PublishInterface<T>(string name, uint version, in T iface) where T : unmanaged
    {
        var p = (T*)NativeMemory.Alloc((nuint)sizeof(T));
        *p = iface;
        PublishInterface(name, version, p);
        return p;
    }

    // ---- Hooks (API 3, 7) -------------------------------------------------

    /// <summary>
    /// An inline hook at the start of the function at <paramref name="target"/>. <paramref name="detour"/>
    /// is an <c>[UnmanagedCallersOnly]</c> method with the same signature: <c>(nint)(delegate* unmanaged&lt;...&gt;)&amp;MyDetour</c>.
    /// Null if refused (the log says why). Game code needs <see cref="ModPlatform.WaitForCode"/> first.
    /// </summary>
    public Hook? HookFunction(string? name, nint target, nint detour)
    {
        ModPlatform.Require(ApiLevel.Hooks, nameof(HookFunction));
        var slot = Hook.NewSlot();
        using var u = new Utf8Scope();
        var h = Api->HookFunction(H, u.Str(name), (void*)target, (void*)detour, slot);
        return Made(h, name, slot);
    }

    /// <summary>One CALL rel32 or JMP rel32 at <paramref name="callSite"/>: only that call goes to the detour.</summary>
    public Hook? HookCall(string? name, nint callSite, nint detour)
    {
        ModPlatform.Require(ApiLevel.Hooks, nameof(HookCall));
        var slot = Hook.NewSlot();
        using var u = new Utf8Scope();
        var h = Api->HookCall(H, u.Str(name), (void*)callSite, (void*)detour, slot);
        return Made(h, name, slot);
    }

    /// <summary>A function pointer in memory, like a vtable slot.</summary>
    public Hook? HookPointer(string? name, nint pointerSlot, nint detour)
    {
        ModPlatform.Require(ApiLevel.Hooks, nameof(HookPointer));
        var slot = Hook.NewSlot();
        using var u = new Utf8Scope();
        var h = Api->HookPointer(H, u.Str(name), (void**)pointerSlot, (void*)detour, slot);
        return Made(h, name, slot);
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> (at most 32) at <paramref name="at"/>, which must hold
    /// <paramref name="expect"/> first; an empty <paramref name="expect"/> takes what's there if it matches the exe.
    /// Like every hook, it starts switched off: nothing is written until you call <see cref="EnableHooks"/>.
    /// </summary>
    public Hook? PatchBytes(string? name, nint at, ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> expect = default)
    {
        ModPlatform.Require(ApiLevel.PreciseHooks, nameof(PatchBytes));
        if (!expect.IsEmpty && expect.Length != bytes.Length)
            throw new ArgumentException("expect must be as long as bytes", nameof(expect));
        using var u = new Utf8Scope();
        fixed (byte* b = bytes)
        fixed (byte* e = expect)
        {
            var h = Api->PatchBytes(H, u.Str(name), (void*)at, expect.IsEmpty ? null : e, b, (uint)bytes.Length);
            return Made(h, name, null);
        }
    }

    /// <summary>
    /// Calls <paramref name="handler"/> with the registers when the game reaches the instruction at
    /// <paramref name="at"/>. Runs on the game's thread, often every frame. For the hottest paths, use
    /// the overload that takes an <c>[UnmanagedCallersOnly]</c> function pointer instead.
    /// </summary>
    public Hook? HookInstruction(string? name, nint at, InstructionHandler handler)
    {
        ModPlatform.Require(ApiLevel.PreciseHooks, nameof(HookInstruction));
        var user = Root(handler);
        using var u = new Utf8Scope();
        var h = Api->HookInstruction(H, u.Str(name), (void*)at, &Callbacks.Instruction, user);
        return Made(h, name, null);
    }

    /// <summary>An instruction hook straight to native-callable code: no managed delegate in between.</summary>
    public Hook? HookInstruction(string? name, nint at, delegate* unmanaged<FH2Context*, void*, void> fn, void* user = null)
    {
        ModPlatform.Require(ApiLevel.PreciseHooks, nameof(HookInstruction));
        using var u = new Utf8Scope();
        var h = Api->HookInstruction(H, u.Str(name), (void*)at, fn, user);
        return Made(h, name, null);
    }

    private Hook? Made(FH2Hook* h, string? name, void** slot)
    {
        if (h is null)
        {
            if (slot is not null) NativeMemory.Free(slot);
            return null;
        }
        var hook = new Hook(this, h, name, slot);
        lock (_hooks) _hooks.Add(hook);
        return hook;
    }

    /// <summary>Switches <paramref name="hook"/> on, or every hook of yours (in one go) if null.</summary>
    public bool EnableHooks(Hook? hook = null)
    {
        ModPlatform.Require(ApiLevel.Hooks, nameof(EnableHooks));
        if (hook is null && Slot.HasRetiredHooks) return EachHook(Api->EnableHooks);
        return Api->EnableHooks(H, hook is null ? null : hook.Handle) != 0;
    }

    public bool DisableHooks(Hook? hook = null)
    {
        ModPlatform.Require(ApiLevel.Hooks, nameof(DisableHooks));
        if (hook is null && Slot.HasRetiredHooks) return EachHook(Api->DisableHooks);
        return Api->DisableHooks(H, hook is null ? null : hook.Handle) != 0;
    }

    // After a reload the handle also holds the old copy's hooks (off, pointing at old code), so
    // "all of mine" has to mean this copy's, one by one.
    private bool EachHook(delegate* unmanaged<FH2Mod*, FH2Hook*, int> fn)
    {
        bool ok = true;
        lock (_hooks)
            foreach (var h in _hooks) ok &= fn(H, h.Handle) != 0;
        return ok;
    }

    // ---- Sessions, snapshots, dumps (API 9) -------------------------------

    /// <summary>
    /// &lt;azimuth_dir&gt;\&lt;folder&gt;\logs\&lt;session id&gt;.log, with the folder made and only the newest
    /// <paramref name="keep"/> logs kept. Null if the folder can't be made.
    /// </summary>
    public string? SessionLogPath(string folder, uint keep = 10)
    {
        ModPlatform.Require(ApiLevel.TitleEntries, nameof(SessionLogPath));
        fixed (char* f = folder) return NativeString.FromUtf16(Api->SessionLogPath(H, f, keep));
    }

    /// <summary>Asks for a copy of the saves folder before the game reads it. Only from OnInit.</summary>
    public bool RequestSaveSnapshot(string reason, uint keep = 5)
    {
        ModPlatform.Require(ApiLevel.TitleEntries, nameof(RequestSaveSnapshot));
        using var u = new Utf8Scope();
        return Api->RequestSaveSnapshot(H, u.Str(reason), keep) != 0;
    }

    /// <summary>Adds native memory (which must stay allocated) to every crash minidump from now on.</summary>
    public void AddDumpMemory(void* p, nuint size)
    {
        ModPlatform.Require(ApiLevel.TitleEntries, nameof(AddDumpMemory));
        Api->AddDumpMemory(H, p, size);
    }

    /// <summary>The key of your title entry that started the game, or null.</summary>
    public string? TitleStartedBy
    {
        get
        {
            ModPlatform.Require(ApiLevel.TitleEntries, nameof(TitleStartedBy));
            return NativeString.FromUtf8(Api->TitleStartedBy(H));
        }
    }
}
