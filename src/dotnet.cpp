// FH2DotNet: runs FH2 mods written in C# (or any .NET language) from
// fh2-mods-dotnet, next to fh2-mods. A mod of the FH2 Mod Platform like any other: it
// sits in fh2-mods as FH2DotNet.dll and the platform calls its fh2mod_init.
//
// From there it starts the .NET runtime (through hostfxr, as `dotnet` does),
// loads the SDK assembly next to it and hands over to its HostEntry.Start,
// which starts every mod in fh2-mods-dotnet, each registering with the
// platform under its own name. All of it happens inside fh2mod_init, so
// their text, file and database edits are in time like any native mod's.
//
//   fh2-mods\FH2DotNet.dll                          this
//   fh2-mods\dotnet\FH2ModPlatform.dll              the SDK, shared by every mod
//   fh2-mods\dotnet\FH2ModPlatform.runtimeconfig.json
//   fh2-mods-dotnet\<Name>\<Name>.dll               the mods, with their dependencies (next to fh2-mods)
//   fh2-mods\dotnet\runtime\                        optional: a .NET runtime of its own
//
// The runtime is the one in dotnet\runtime if there is one, else the
// installed one (DOTNET_ROOT, the registry, then Program Files\dotnet).
// Without any .NET mods the runtime isn't started at all.
#include "fh2mod.h"

#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#ifndef FH2DOTNET_VERSION
#define FH2DOTNET_VERSION "0.0.0"
#endif

namespace fs = std::filesystem;

namespace {

// ---- hostfxr, from the .NET runtime's hostfxr.h and coreclr_delegates.h ----
// (Declared here rather than included: they're a few lines, stable since
// .NET 5, and the .NET SDK doesn't ship the headers with llvm-mingw in mind.)

using hostfxr_handle = void*;
struct hostfxr_initialize_parameters {
    size_t size;
    const wchar_t* host_path;
    const wchar_t* dotnet_root;
};
enum hostfxr_delegate_type {
    hdt_get_function_pointer = 6,   // .NET 5
    hdt_load_assembly        = 7,   // .NET 8: into the default load context
};
using hostfxr_initialize_for_runtime_config_fn = int32_t(__cdecl*)(const wchar_t* runtime_config_path,
                                                                   const hostfxr_initialize_parameters* parameters,
                                                                   hostfxr_handle* host_context_handle);
using hostfxr_get_runtime_delegate_fn          = int32_t(__cdecl*)(hostfxr_handle, hostfxr_delegate_type, void**);
using hostfxr_close_fn                         = int32_t(__cdecl*)(hostfxr_handle);
using hostfxr_error_writer_fn                  = void(__cdecl*)(const wchar_t* message);
using hostfxr_set_error_writer_fn              = hostfxr_error_writer_fn(__cdecl*)(hostfxr_error_writer_fn);

using load_assembly_fn        = int(__stdcall*)(const wchar_t* assembly_path, void* load_context, void* reserved);
using get_function_pointer_fn = int(__stdcall*)(const wchar_t* type_name, const wchar_t* method_name,
                                                const wchar_t* delegate_type_name, void* load_context,
                                                void* reserved, void** delegate);
const wchar_t* const UNMANAGEDCALLERSONLY_METHOD = reinterpret_cast<const wchar_t*>(-1);

// What the hostfxr codes mean, for the log. Success_HostAlreadyInitialized (1) and
// Success_DifferentRuntimeProperties (2) are successes too.
const char* hostfxr_error(int32_t rc) {
    switch (static_cast<uint32_t>(rc)) {
        case 0x80008083: return "the .NET version it needs isn't installed";
        case 0x80008093: return "FH2ModPlatform.runtimeconfig.json is invalid";
        case 0x80008094: return "the .NET runtime is missing";
        case 0x80008096: return "the runtime is already running with other settings";
        default: return nullptr;
    }
}

// HostEntry in FH2ModPlatform.dll. Start returns how many mods failed; Command runs "reload
// [name]" and "mods"; SetAutoReload watches for rebuilt mods.
using start_fn           = int(__stdcall*)(const FH2ModApi* api, FH2Mod* host, const wchar_t* mods_dir);
using command_fn         = void(__stdcall*)(const char* command, const char* args, char* reply, size_t reply_size);
using set_auto_reload_fn = void(__stdcall*)(int on);

const FH2ModApi* g_api = nullptr;
FH2Mod* g_mod          = nullptr;
std::atomic<command_fn> g_command{nullptr};
std::atomic<set_auto_reload_fn> g_set_auto_reload{nullptr};

std::string narrow(const std::wstring& w) {
    if (w.empty()) return {};
    const int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), nullptr, 0, nullptr, nullptr);
    std::string s(static_cast<size_t>(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), s.data(), n, nullptr, nullptr);
    return s;
}

void log(const std::string& line) { g_api->log(g_mod, line.c_str()); }

void __cdecl log_hostfxr_error(const wchar_t* message) {
    if (message) log("hostfxr: " + narrow(message));
}

// "10.0.2" < "10.0.13"; a release beats its previews ("10.0.0-rc.1").
bool version_less(const std::wstring& a, const std::wstring& b) {
    auto parts = [](const std::wstring& v, bool& pre) {
        std::vector<long> n;
        size_t i = 0;
        while (i < v.size() && iswdigit(v[i])) {
            n.push_back(std::wcstol(v.c_str() + i, nullptr, 10));
            while (i < v.size() && iswdigit(v[i])) ++i;
            if (i < v.size() && v[i] == L'.') ++i;
        }
        pre = i < v.size();
        return n;
    };
    bool pa = false, pb = false;
    const auto na = parts(a, pa), nb = parts(b, pb);
    if (na != nb) return na < nb;
    return pa && !pb;
}

// <root>\host\fxr\<highest version>\hostfxr.dll, or empty.
fs::path hostfxr_in(const fs::path& root) {
    std::error_code ec;
    const fs::path fxr = root / L"host" / L"fxr";
    if (!fs::is_directory(fxr, ec)) return {};
    std::wstring best;
    for (const auto& e : fs::directory_iterator(fxr, ec)) {
        if (!e.is_directory(ec) || !fs::is_regular_file(e.path() / L"hostfxr.dll", ec)) continue;
        const auto v = e.path().filename().wstring();
        if (best.empty() || version_less(best, v)) best = v;
    }
    return best.empty() ? fs::path{} : fxr / best / L"hostfxr.dll";
}

std::wstring env(const wchar_t* name) {
    wchar_t buf[MAX_PATH * 2];
    const DWORD n = GetEnvironmentVariableW(name, buf, static_cast<DWORD>(std::size(buf)));
    return n > 0 && n < std::size(buf) ? std::wstring(buf, n) : std::wstring();
}

// Where .NET is: the folder with host\fxr in it. `local` is checked first.
fs::path find_dotnet_root(const fs::path& local) {
    std::vector<fs::path> candidates{local};
    if (auto r = env(L"DOTNET_ROOT"); !r.empty()) candidates.emplace_back(r);
    {
        // Where the .NET installer says it put the x64 runtime (the 32-bit view, as nethost reads it).
        wchar_t buf[MAX_PATH * 2];
        DWORD size = sizeof buf;
        if (RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\dotnet\\Setup\\InstalledVersions\\x64", L"InstallLocation",
                         RRF_RT_REG_SZ | RRF_SUBKEY_WOW6432KEY, nullptr, buf, &size) == ERROR_SUCCESS)
            candidates.emplace_back(buf);
    }
    if (auto pf = env(L"ProgramW6432"); !pf.empty()) candidates.emplace_back(fs::path(pf) / L"dotnet");
    if (auto pf = env(L"ProgramFiles"); !pf.empty()) candidates.emplace_back(fs::path(pf) / L"dotnet");
    for (const auto& c : candidates)
        if (!c.empty() && !hostfxr_in(c).empty()) return c;
    return {};
}

// True if fh2-mods-dotnet has at least one <Name>\<Name>.dll to start.
bool any_mods(const fs::path& mods) {
    std::error_code ec;
    if (!fs::is_directory(mods, ec)) return false;
    for (const auto& e : fs::directory_iterator(mods, ec)) {
        if (!e.is_directory(ec)) continue;
        const auto name = e.path().filename().wstring();
        if (fs::is_regular_file(e.path() / (name + L".dll"), ec)) return true;
    }
    // Scripts: scripts\<Name>.cs or .vb, compiled by the SDK when the game starts.
    for (const auto& e : fs::directory_iterator(mods / L"scripts", ec)) {
        auto ext = e.path().extension().wstring();
        for (auto& c : ext) c = static_cast<wchar_t>(towlower(c));
        if (e.is_regular_file(ec) && (ext == L".cs" || ext == L".vb")) return true;
    }
    return false;
}

// True for a managed .NET assembly (a PE file with a CLR header): a hosted mod's build.
bool is_managed_assembly(const fs::path& file) {
    HANDLE f = CreateFileW(file.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return false;
    unsigned char buf[4096];
    DWORD got = 0;
    const BOOL ok = ReadFile(f, buf, sizeof buf, &got, nullptr);
    CloseHandle(f);
    if (!ok || got < sizeof(IMAGE_DOS_HEADER)) return false;
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(buf);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0 ||
        static_cast<DWORD>(dos->e_lfanew) + sizeof(IMAGE_NT_HEADERS64) > got)
        return false;
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS32*>(buf + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return false;
    // AnyCPU builds are PE32, x64 builds PE32+: the data directories sit at different offsets.
    const IMAGE_DATA_DIRECTORY* clr = nullptr;
    if (nt->OptionalHeader.Magic == IMAGE_NT_OPTIONAL_HDR32_MAGIC)
        clr = &nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR];
    else if (nt->OptionalHeader.Magic == IMAGE_NT_OPTIONAL_HDR64_MAGIC)
        clr = &reinterpret_cast<const IMAGE_NT_HEADERS64*>(nt)->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR];
    return clr && clr->VirtualAddress != 0 && clr->Size != 0;
}

// A hosted mod's build copied straight into fh2-mods: the platform can't start it (it has no
// fh2mod_init) and lists it as a mod from before the platform. Say where it goes.
void check_misplaced_mods() {
    const fs::path mods = fs::path(g_api->azimuth_dir) / L"fh2-mods";
    std::error_code ec;
    std::string names;
    for (const auto& e : fs::directory_iterator(mods, ec)) {
        if (!e.is_regular_file(ec)) continue;
        auto ext = e.path().extension().wstring();
        for (auto& c : ext) c = static_cast<wchar_t>(towlower(c));
        if (ext != L".dll" || !is_managed_assembly(e.path())) continue;
        const auto stem = e.path().stem().wstring();
        log("WRONG FOLDER: " + narrow(e.path().filename().wstring()) +
            " is a hosted .NET mod, which can't run from fh2-mods itself. Move it (with its .deps.json and "
            "other files) to fh2-mods-dotnet\\" + narrow(stem) + "\\, or build it as a native DLL with "
            "dotnet publish -p:FH2ModHosting=Aot");
        names += (names.empty() ? "" : ", ") + narrow(e.path().filename().wstring());
    }
    if (!names.empty()) {
        for (auto& c : names) c = static_cast<char>(toupper(static_cast<unsigned char>(c)));
        g_api->set_notice(g_mod, (".NET MOD IN WRONG FOLDER: " + names).c_str());
    }
}

fs::path this_dll() {
    HMODULE self = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       reinterpret_cast<LPCWSTR>(&this_dll), &self);
    wchar_t buf[MAX_PATH * 2];
    const DWORD n = GetModuleFileNameW(self, buf, static_cast<DWORD>(std::size(buf)));
    return n ? fs::path(std::wstring(buf, n)) : fs::path{};
}

int start() {
    const fs::path dir     = fs::path(g_api->azimuth_dir) / L"fh2-mods" / L"dotnet";
    const fs::path mods    = fs::path(g_api->azimuth_dir) / L"fh2-mods-dotnet";
    // Where hosted mods were before: they don't load from there any more.
    if (any_mods(dir / L"mods"))
        log("fh2-mods\\dotnet\\mods has .NET mods, which no longer load from there: move each mod's folder "
            "to fh2-mods-dotnet (next to fh2-mods)");
    const fs::path sdk     = dir / L"FH2ModPlatform.dll";
    const fs::path config  = dir / L"FH2ModPlatform.runtimeconfig.json";
    std::error_code ec;

    if (!any_mods(mods)) {
        log("no .NET mods in fh2-mods-dotnet; the .NET runtime isn't started");
        return 0;
    }
    if (!fs::is_regular_file(sdk, ec) || !fs::is_regular_file(config, ec)) {
        log("FAILED: fh2-mods\\dotnet\\FH2ModPlatform.dll or its runtimeconfig.json is missing; reinstall a .NET mod");
        g_api->set_notice(g_mod, ".NET MODS: REINSTALL ONE OF THEM");
        return 1;
    }

    const fs::path root = find_dotnet_root(dir / L"runtime");
    if (root.empty()) {
        log("FAILED: .NET isn't installed, so .NET mods can't run. Install the .NET Desktop Runtime 10 (x64) "
            "from https://dotnet.microsoft.com/download/dotnet/10.0");
        g_api->set_notice(g_mod, ".NET MODS NEED .NET 10 (X64)");
        return 1;
    }
    const fs::path fxr_path = hostfxr_in(root);
    HMODULE fxr = LoadLibraryExW(fxr_path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!fxr) {
        log("FAILED to load " + narrow(fxr_path.wstring()) + " (error " + std::to_string(GetLastError()) + ")");
        return 1;
    }
    log("using .NET from " + narrow(root.wstring()));

    auto init  = reinterpret_cast<hostfxr_initialize_for_runtime_config_fn>(
        reinterpret_cast<void*>(GetProcAddress(fxr, "hostfxr_initialize_for_runtime_config")));
    auto get   = reinterpret_cast<hostfxr_get_runtime_delegate_fn>(
        reinterpret_cast<void*>(GetProcAddress(fxr, "hostfxr_get_runtime_delegate")));
    auto close = reinterpret_cast<hostfxr_close_fn>(reinterpret_cast<void*>(GetProcAddress(fxr, "hostfxr_close")));
    auto set_writer = reinterpret_cast<hostfxr_set_error_writer_fn>(
        reinterpret_cast<void*>(GetProcAddress(fxr, "hostfxr_set_error_writer")));
    if (!init || !get || !close) {
        log("FAILED: " + narrow(fxr_path.wstring()) + " is too old (it needs .NET 8 or newer)");
        return 1;
    }

    // hostfxr's own explanations (which version was wanted, what's installed) go to the log.
    hostfxr_error_writer_fn old_writer = set_writer ? set_writer(log_hostfxr_error) : nullptr;
    struct RestoreWriter {
        hostfxr_set_error_writer_fn set;
        hostfxr_error_writer_fn old;
        ~RestoreWriter() { if (set) set(old); }
    } restore{set_writer, old_writer};

    const std::wstring host_path = this_dll().wstring();
    const std::wstring root_str  = root.wstring();
    hostfxr_initialize_parameters params{sizeof params, host_path.c_str(), root_str.c_str()};
    hostfxr_handle ctx = nullptr;
    const int32_t rc   = init(config.c_str(), &params, &ctx);
    if (rc < 0 || !ctx) {
        char code[16];
        std::snprintf(code, sizeof code, "0x%08X", static_cast<unsigned>(rc));
        const char* why = hostfxr_error(rc);
        log(std::string("FAILED to start the .NET runtime (") + code + (why ? std::string(": ") + why : "") + ")");
        if (static_cast<uint32_t>(rc) == 0x80008083) g_api->set_notice(g_mod, ".NET MODS NEED .NET 10 (X64)");
        if (ctx) close(ctx);
        return 1;
    }
    if (rc != 0) log("the .NET runtime was already running in this process; using it");

    load_assembly_fn load_assembly         = nullptr;
    get_function_pointer_fn get_function   = nullptr;
    const int32_t r1 = get(ctx, hdt_load_assembly, reinterpret_cast<void**>(&load_assembly));
    const int32_t r2 = get(ctx, hdt_get_function_pointer, reinterpret_cast<void**>(&get_function));
    close(ctx);   // the runtime stays; only the setup handle goes
    if (r1 < 0 || r2 < 0 || !load_assembly || !get_function) {
        log("FAILED: this .NET runtime can't load components (it needs .NET 8 or newer)");
        return 1;
    }

    if (int r = load_assembly(sdk.c_str(), nullptr, nullptr); r < 0) {
        log("FAILED to load FH2ModPlatform.dll (error " + std::to_string(r) + ")");
        return 1;
    }
    start_fn entry = nullptr;
    const int r3 = get_function(L"FH2ModPlatform.Hosting.HostEntry, FH2ModPlatform", L"Start",
                                UNMANAGEDCALLERSONLY_METHOD, nullptr, nullptr, reinterpret_cast<void**>(&entry));
    if (r3 < 0 || !entry) {
        log("FAILED to find HostEntry.Start in FH2ModPlatform.dll (error " + std::to_string(r3) +
            "); it may be from an older SDK");
        return 1;
    }
    command_fn command               = nullptr;
    set_auto_reload_fn auto_reload   = nullptr;
    get_function(L"FH2ModPlatform.Hosting.HostEntry, FH2ModPlatform", L"Command", UNMANAGEDCALLERSONLY_METHOD,
                 nullptr, nullptr, reinterpret_cast<void**>(&command));
    get_function(L"FH2ModPlatform.Hosting.HostEntry, FH2ModPlatform", L"SetAutoReload",
                 UNMANAGEDCALLERSONLY_METHOD, nullptr, nullptr, reinterpret_cast<void**>(&auto_reload));

    // From here on, the SDK logs for itself.
    const int failed = entry(g_api, g_mod, mods.c_str());
    g_command        = command;
    g_set_auto_reload = auto_reload;
    return failed == 0 ? 0 : 1;
}

// ---- Settings, kept in fh2-mods\dotnet\FH2DotNet.ini (the platform doesn't store values) ----

struct Prefs {
    int console     = 0;
    int auto_reload = 0;
    uint32_t vk = 0, modifiers = 0, pad = 0;   // the reload hotkey; 0 = none
    // The console hotkey: the key left of 1 (` and ~ on a US keyboard) unless rebound.
    uint32_t console_vk = 0xC0 /* VK_OEM_3 */, console_modifiers = 0, console_pad = 0;
} g_prefs;

std::wstring ini_path() { return (fs::path(g_api->azimuth_dir) / L"fh2-mods" / L"dotnet" / L"FH2DotNet.ini").wstring(); }

void load_prefs() {
    const auto p     = ini_path();
    auto get = [&](const wchar_t* k, UINT def = 0) { return GetPrivateProfileIntW(L"FH2DotNet", k, def, p.c_str()); };
    g_prefs.console     = get(L"Console") != 0;
    g_prefs.auto_reload = get(L"AutoReload") != 0;
    g_prefs.vk          = get(L"ReloadKey");
    g_prefs.modifiers   = get(L"ReloadModifiers");
    g_prefs.pad         = get(L"ReloadPad");
    g_prefs.console_vk        = get(L"ConsoleKey", 0xC0);
    g_prefs.console_modifiers = get(L"ConsoleModifiers");
    g_prefs.console_pad       = get(L"ConsolePad");
}

void save_prefs() {
    const auto p = ini_path();
    std::error_code ec;
    fs::create_directories(fs::path(p).parent_path(), ec);
    auto put = [&](const wchar_t* k, uint32_t v) {
        WritePrivateProfileStringW(L"FH2DotNet", k, std::to_wstring(v).c_str(), p.c_str());
    };
    put(L"Console", g_prefs.console);
    put(L"AutoReload", g_prefs.auto_reload);
    put(L"ReloadKey", g_prefs.vk);
    put(L"ReloadModifiers", g_prefs.modifiers);
    put(L"ReloadPad", g_prefs.pad);
    put(L"ConsoleKey", g_prefs.console_vk);
    put(L"ConsoleModifiers", g_prefs.console_modifiers);
    put(L"ConsolePad", g_prefs.console_pad);
}

// ---- The console window: FH2Console.exe, a process of its own -------------------
// Not a console in the game's process: closing a console window ends the process it
// belongs to, and that mustn't be the game. FH2Console.exe follows platform.log and
// runs commands through the platform's pipe; it closes itself when the game does.

std::mutex g_console_mu;
HANDLE g_console = nullptr;

void open_console() {
    std::scoped_lock lk{g_console_mu};
    if (g_console) return;
    const fs::path dir = fs::path(g_api->azimuth_dir) / L"fh2-mods";
    const fs::path exe = dir / L"dotnet" / L"FH2Console.exe";
    std::error_code ec;
    if (!fs::is_regular_file(exe, ec)) {
        log("can't open the console: fh2-mods\\dotnet\\FH2Console.exe is missing");
        return;
    }
    std::wstring cmd = L"\"" + exe.wstring() + L"\" --pid " + std::to_wstring(GetCurrentProcessId()) + L" --log \"" +
                       (dir / L"platform.log").wstring() + L"\" --key " + std::to_wstring(g_prefs.console_vk);
    STARTUPINFOW si{};
    si.cb = sizeof si;
    // Shown beside the game without taking the focus (and the controller) from it.
    si.dwFlags     = STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_SHOWNOACTIVATE;
    PROCESS_INFORMATION pi{};
    if (!CreateProcessW(exe.c_str(), cmd.data(), nullptr, nullptr, FALSE, CREATE_NEW_CONSOLE, nullptr,
                        exe.parent_path().c_str(), &si, &pi)) {
        log("can't open the console (error " + std::to_string(GetLastError()) + ")");
        return;
    }
    CloseHandle(pi.hThread);
    g_console = pi.hProcess;
    std::thread([h = pi.hProcess] {
        WaitForSingleObject(h, INFINITE);
        bool by_player = false;
        {
            std::scoped_lock lk{g_console_mu};
            if (g_console == h) {
                g_console = nullptr;
                by_player = true;
            }
        }
        CloseHandle(h);
        if (by_player) {
            // Closed from its own window: the switch follows.
            g_prefs.console = 0;
            save_prefs();
            if (g_api->api_version >= 4) g_api->set_setting(g_mod, "Console", 0);
        }
    }).detach();
}

void close_console() {
    std::scoped_lock lk{g_console_mu};
    if (!g_console) return;
    HANDLE h  = g_console;
    g_console = nullptr;   // the watcher closes the handle
    TerminateProcess(h, 0);
}

// ---- Callbacks from the platform ----

void set_console(bool on, bool from_setting) {
    g_prefs.console = on;
    save_prefs();
    if (on) open_console();
    else close_console();
    if (!from_setting && g_api->api_version >= 4) g_api->set_setting(g_mod, "Console", on ? 1 : 0);
}

void __cdecl on_console(void*, const char*, int value) { set_console(value != 0, true); }

// The console key, while the game is in front (in the console's own window, FH2Console.exe
// handles the same key itself).
void __cdecl on_console_key(void*, const char*) {
    bool open;
    {
        std::scoped_lock lk{g_console_mu};
        open = g_console != nullptr;
    }
    set_console(!open, false);
}

void __cdecl on_console_rebind(void*, const char*, uint32_t vk, uint32_t modifiers, uint32_t pad) {
    g_prefs.console_vk        = vk;
    g_prefs.console_modifiers = modifiers;
    g_prefs.console_pad       = pad;
    save_prefs();
}

void __cdecl on_auto_reload(void*, const char*, int value) {
    g_prefs.auto_reload = value != 0;
    save_prefs();
    if (auto f = g_set_auto_reload.load()) f(value);
}

void run_command(const char* name, const char* args, char* reply, size_t reply_size) {
    if (auto f = g_command.load()) {
        f(name, args ? args : "", reply, reply_size);
        return;
    }
    std::snprintf(reply, reply_size, "%s",
                  "error: no hosted .NET mods are running (the runtime only starts when fh2-mods-dotnet has "
                  "a mod at startup)");
}

void __cdecl on_command(void* user, const char*, const char* args, char* reply, size_t reply_size) {
    run_command(static_cast<const char*>(user), args, reply, reply_size);
}

void __cdecl on_reload_key(void*, const char*) {
    char reply[2048];
    run_command("reload", "", reply, sizeof reply);   // HostEntry logs what it did
}

void __cdecl on_reload_rebind(void*, const char*, uint32_t vk, uint32_t modifiers, uint32_t pad) {
    g_prefs.vk        = vk;
    g_prefs.modifiers = modifiers;
    g_prefs.pad       = pad;
    save_prefs();
}

void add_controls() {
    if (g_api->api_version >= 4) {
        FH2Setting s{};
        s.size        = g_api->api_version >= 9 ? sizeof s : offsetof(FH2Setting, kind);
        s.key         = "Console";
        s.label       = "Console window";
        s.description = "A window next to the game with platform.log as it's written (every mod's lines, and .NET "
                        "mods' Console output), and a prompt for mods' commands: try \"help\".";
        s.value       = g_prefs.console;
        s.on_change   = on_console;
        g_api->add_setting(g_mod, &s);

        s.key         = "AutoReload";
        s.label       = "Reload .NET mods when rebuilt";
        s.description = "For mod makers: when a hosted mod's DLL in fh2-mods-dotnet changes, start the new "
                        "build straight away, without restarting the game.";
        s.value       = g_prefs.auto_reload;
        s.on_change   = on_auto_reload;
        g_api->add_setting(g_mod, &s);
    }
    if (g_api->api_version >= 9) {
        FH2Hotkey c{};
        c.size      = sizeof c;
        c.key       = "ConsoleKey";
        c.label     = "Open or close the console";
        c.vk        = g_prefs.console_vk;
        c.modifiers = g_prefs.console_modifiers;
        c.pad_chord = g_prefs.console_pad;
        c.on_press  = on_console_key;
        c.on_rebind = on_console_rebind;
        g_api->add_hotkey(g_mod, &c);

        FH2Hotkey k{};
        k.size      = sizeof k;
        k.key       = "Reload";
        k.label     = "Reload .NET mods";
        k.vk        = g_prefs.vk;
        k.modifiers = g_prefs.modifiers;
        k.pad_chord = g_prefs.pad;
        k.on_press  = on_reload_key;
        k.on_rebind = on_reload_rebind;
        g_api->add_hotkey(g_mod, &k);

        static const char* const kReload = "reload";
        static const char* const kMods   = "mods";
        g_api->add_command(g_mod, "reload", "[mod] starts a rebuilt hosted .NET mod again (or all of them)", on_command,
                           const_cast<char*>(kReload));
        g_api->add_command(g_mod, "mods", "lists the hosted .NET mods and what they're doing", on_command,
                           const_cast<char*>(kMods));
    }
}

} // namespace

FH2MOD_EXPORT int fh2mod_init(const FH2ModApi* api) {
    if (!api || api->api_version < 1) return 1;
    g_api = api;

    FH2ModInfo info{};
    info.size             = api->api_version >= 6 ? sizeof info : offsetof(FH2ModInfo, description);
    info.name             = "Platform.NET";   // the MODS tab row; the DLL stays FH2DotNet.dll
    info.version          = FH2DOTNET_VERSION;
    info.github_repo      = nullptr;
    info.platform_version = FH2MOD_PLATFORM_VERSION;
    info.check_updates    = 0;
    info.description      = "Runs the mods written in C# and VB.NET (hosted .NET mods) from fh2-mods-dotnet. "
                            "They're listed under Hosted .NET mods below.";
    g_mod                 = api->register_mod(&info);
    if (!g_mod) return 1;

    try {
        load_prefs();
        add_controls();
        check_misplaced_mods();
        if (g_prefs.console) open_console();   // before the runtime, so the console shows it starting
        const int rc = start();
        if (g_prefs.auto_reload)
            if (auto f = g_set_auto_reload.load()) f(1);
        return rc;
    } catch (const std::exception& e) {
        log(std::string("FAILED: ") + e.what());
        return 1;
    }
}
