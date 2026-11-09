// A stand-in for the FH2 Mod Platform, to test .NET mods without the game:
//   fake_platform.exe <azimuth_dir> <mod.dll>... [-- <script line>...]
// Loads each DLL and calls its fh2mod_init with an FH2ModApi that prints
// what mods do, the way the platform loads fh2-mods. Then it plays the game's
// part: flips every On/Off or choice setting, moves every slider, presses
// every hotkey and runs every command with "world", so the callbacks run
// through the same native-to-managed path they do in the game.
// Exits 0 if every fh2mod_init returned 0.
#include "fh2mod.h"

#include <windows.h>

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

struct FH2Mod {
    std::string name;
};

namespace {

std::mutex g_mu;
std::vector<FH2Mod*> g_mods;

struct Setting {
    FH2Mod* mod;
    std::string key;
    int kind;
    FH2SettingChangedFn on_change;
    FH2SliderChangedFn on_slide;
    void* user;
};
struct Command {
    FH2Mod* mod;
    std::string name;
    FH2CommandFn fn;
    void* user;
};
struct Hotkey {
    FH2Mod* mod;
    std::string key;
    FH2HotkeyFn fn;
    void* user;
};
struct Page {
    FH2Mod* mod;
    std::string key;
    FH2PageOpenFn on_open;
    FH2PageSelectFn on_select;
    void* user;
};
std::vector<Page> g_pages;
std::vector<Setting> g_settings;
std::vector<Command> g_commands;
std::vector<Hotkey> g_hotkeys;
std::wstring g_azimuth;

std::string narrow(const wchar_t* w) {
    if (!w) return "(null)";
    const int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, nullptr, 0, nullptr, nullptr);
    std::string s(static_cast<size_t>(n > 0 ? n - 1 : 0), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w, -1, s.data(), n, nullptr, nullptr);
    return s;
}

const char* nm(FH2Mod* m) { return m ? m->name.c_str() : "?"; }
#define S(x) ((x) ? (x) : "(null)")

FH2Mod* api_register(const FH2ModInfo* info) {
    auto* m = new FH2Mod{info->name};
    std::printf("[platform] register %s %s (platform %s, repo %s, offline_only %d)\n", info->name, S(info->version),
                S(info->platform_version), S(info->github_repo),
                info->size > offsetof(FH2ModInfo, offline_only) ? info->offline_only : 0);
    std::scoped_lock lk{g_mu};
    g_mods.push_back(m);
    return m;
}
void api_log(FH2Mod* m, const char* line) { std::printf("[%s] %s\n", nm(m), S(line)); }
void api_set_string(FH2Mod* m, const char* table, uint16_t key, const wchar_t* text) {
    std::printf("[platform] %s set_string %s 0x%04X = \"%s\"\n", nm(m), table, key, narrow(text).c_str());
}
void api_replace_string(FH2Mod* m, const char* table, const wchar_t* from, const wchar_t* to) {
    std::printf("[platform] %s replace_string %s \"%s\" -> \"%s\"\n", nm(m), table, narrow(from).c_str(), narrow(to).c_str());
}
void api_clear_strings(FH2Mod* m, const char* table) { std::printf("[platform] %s clear_strings %s\n", nm(m), S(table)); }
void api_apply_strings(FH2Mod* m) { std::printf("[platform] %s apply_strings\n", nm(m)); }
void api_set_notice(FH2Mod* m, const char* lines) { std::printf("[platform] %s set_notice \"%s\"\n", nm(m), S(lines)); }
void* api_game_base() { return nullptr; }
void* api_wait_for_game(uint32_t) { return nullptr; }
int api_set_file(FH2Mod* m, const char* path, const void*, size_t size) {
    std::printf("[platform] %s set_file %s (%zu bytes)\n", nm(m), path, size);
    return 1;
}
void api_clear_file(FH2Mod*, const char*) {}
void api_add_sql(FH2Mod* m, const char* sql) { std::printf("[platform] %s add_sql %s\n", nm(m), S(sql)); }
void api_clear_sql(FH2Mod*) {}
void api_apply_database(FH2Mod*) {}
void api_publish_interface(FH2Mod* m, const char* name, uint32_t v, const void*) {
    std::printf("[platform] %s publish_interface %s v%u\n", nm(m), name, v);
}
// A stand-in for FH2 ANNA's menu interface (FH2ANNA/include/fh2anna.h, version 5): prints each entry.
struct FakeAnnaEntry {
    uint32_t size;
    const char *key, *parent, *label;
    void (*on_choose)(void*, const char*);
    int (*shown)(void*, const char*);
    const char* (*label_now)(void*, const char*);
    void* user;
    int stays_open;
    int (*enabled)(void*, const char*);
};
struct FakeAnnaApi {
    uint32_t size;
    int (*is_open)();
    int (*uses_key)(uint32_t);
    int (*add_entry)(const FakeAnnaEntry*);
    void (*hold_input)(int);
};
int anna_is_open() { return 0; }
int anna_uses_key(uint32_t) { return 0; }
int anna_add_entry(const FakeAnnaEntry* e) {
    std::printf("[platform] ANNA add_entry key=%s parent=%s label=\"%s\" %s%s\n", e->key, S(e->parent), e->label,
                e->on_choose ? "item" : "list", e->stays_open ? " stays_open" : "");
    return 1;
}
void anna_hold_input(int) {}
FakeAnnaApi g_anna{sizeof(FakeAnnaApi), anna_is_open, anna_uses_key, anna_add_entry, anna_hold_input};

const void* api_get_interface(const char* name, uint32_t) {
    return name && std::strcmp(name, "FH2ANNA") == 0 && std::getenv("FH2_FAKE_ANNA") ? &g_anna : nullptr;
}
int api_wait_for_mods(uint32_t) { return 0; }
const char* api_mod_version(const char*) { return nullptr; }
FH2Hook* api_hook(FH2Mod*, const char*, void*, void*, void**) { return nullptr; }
FH2Hook* api_hook_pointer(FH2Mod*, const char*, void**, void*, void**) { return nullptr; }
int api_enable(FH2Mod*, FH2Hook*) { return 0; }
void* api_wait_for_code(uint32_t) { return nullptr; }
int api_add_setting(FH2Mod* m, const FH2Setting* s) {
    std::printf("[platform] %s add_setting %s \"%s\" kind %d value %d slider %g choices %u\n", nm(m), s->key, s->label,
                s->kind, s->value, s->slider_value, s->kind == FH2_SETTING_CHOICE ? s->choice_count : 0);
    for (uint32_t i = 0; s->kind == FH2_SETTING_CHOICE && i < s->choice_count; ++i)
        std::printf("[platform]   choice %u: %s\n", i, s->choices[i]);
    std::scoped_lock lk{g_mu};
    g_settings.push_back({m, s->key, s->kind, s->on_change, s->on_slide, s->user});
    return 1;
}
int api_get_setting(FH2Mod*, const char*) { return -1; }
void api_set_setting(FH2Mod*, const char*, int) {}
float api_get_slider(FH2Mod*, const char*) { return 0; }
void api_set_slider(FH2Mod*, const char*, float) {}
FH2Hook* api_patch(FH2Mod*, const char*, void*, const void*, const void*, uint32_t) { return nullptr; }
FH2Hook* api_hook_instruction(FH2Mod*, const char*, void*, FH2InstructionFn, void*) { return nullptr; }
int api_add_page(FH2Mod* m, const FH2Page* p) {
    std::printf("[platform] %s add_page %s \"%s\"\n", nm(m), p->key, p->title);
    std::scoped_lock lk{g_mu};
    g_pages.push_back({m, p->key, p->on_open, p->on_select, p->user});
    return 1;
}
int api_set_page_rows(FH2Mod* m, const char* key, const FH2PageRow* rows, uint32_t n) {
    std::printf("[platform] %s set_page_rows %s (%u rows)\n", nm(m), key, n);
    for (uint32_t i = 0; i < n; ++i)
        std::printf("[platform]   %s | %s | key %s | style %d alert %d | %s\n", rows[i].label, S(rows[i].value),
                    S(rows[i].key), rows[i].style, rows[i].value_alert, S(rows[i].description));
    return 1;
}
int api_set_page_xaml(FH2Mod*, const char*, const char*, const char*, const char*) { return 1; }
int api_get_page_slot(FH2Mod*, const char*, int) { return 0; }
int api_set_page_slot(FH2Mod*, const char*, int, int) { return 1; }
int api_focus(FH2Mod*, const char*, const char*) { return 1; }
int api_tile_text(FH2Mod*, const char*, const char*) { return 1; }
int api_tile_colors(FH2Mod*, const char*, uint32_t, uint32_t) { return 1; }
int api_add_title_entry(FH2Mod* m, const FH2TitleEntry* e) {
    std::printf("[platform] %s add_title_entry %s \"%s\"\n", nm(m), e->key, e->label);
    return 1;
}
const char* api_title_started_by(FH2Mod*) { return nullptr; }
int api_playing_online() { return 0; }
const char* api_session_id() { return "20261109-000000-0000"; }
const wchar_t* api_session_log_path(FH2Mod*, const wchar_t*, uint32_t) { return nullptr; }
int api_request_save_snapshot(FH2Mod*, const char*, uint32_t) { return 0; }
void api_add_dump_memory(FH2Mod*, const void*, size_t) {}
int api_add_hotkey(FH2Mod* m, const FH2Hotkey* h) {
    std::printf("[platform] %s add_hotkey %s \"%s\" vk 0x%X\n", nm(m), h->key, h->label, h->vk);
    std::scoped_lock lk{g_mu};
    g_hotkeys.push_back({m, h->key, h->on_press, h->user});
    return 1;
}
int api_add_command(FH2Mod* m, const char* name, const char* help, FH2CommandFn fn, void* user) {
    std::printf("[platform] %s add_command %s (%s)\n", nm(m), name, S(help));
    std::scoped_lock lk{g_mu};
    g_commands.push_back({m, name, fn, user});
    return 1;
}

FH2ModApi make_api() {
    FH2ModApi a{};
    a.size                 = sizeof a;
    a.api_version          = FH2MOD_API_VERSION;
    a.platform_version     = FH2MOD_PLATFORM_VERSION;
    a.azimuth_dir          = g_azimuth.c_str();
    static std::wstring game_dir = _wgetenv(L"FH2_GAME_DIR") ? _wgetenv(L"FH2_GAME_DIR") : L"";
    a.game_dir             = game_dir.c_str();
    a.register_mod         = api_register;
    a.log                  = api_log;
    a.set_string           = api_set_string;
    a.replace_string       = api_replace_string;
    a.clear_strings        = api_clear_strings;
    a.apply_strings        = api_apply_strings;
    a.set_notice           = api_set_notice;
    a.game_base            = api_game_base;
    a.wait_for_game        = api_wait_for_game;
    a.set_file             = api_set_file;
    a.clear_file           = api_clear_file;
    a.add_sql              = api_add_sql;
    a.clear_sql            = api_clear_sql;
    a.apply_database       = api_apply_database;
    a.publish_interface    = api_publish_interface;
    a.get_interface        = api_get_interface;
    a.wait_for_mods        = api_wait_for_mods;
    a.mod_version          = api_mod_version;
    a.hook_function        = api_hook;
    a.hook_call            = api_hook;
    a.hook_pointer         = api_hook_pointer;
    a.enable_hooks         = api_enable;
    a.disable_hooks        = api_enable;
    a.wait_for_code        = api_wait_for_code;
    a.add_setting          = api_add_setting;
    a.get_setting          = api_get_setting;
    a.set_setting          = api_set_setting;
    a.get_slider           = api_get_slider;
    a.set_slider           = api_set_slider;
    a.patch_bytes          = api_patch;
    a.hook_instruction     = api_hook_instruction;
    a.add_page             = api_add_page;
    a.set_page_rows        = api_set_page_rows;
    a.set_page_xaml        = api_set_page_xaml;
    a.get_page_slot        = api_get_page_slot;
    a.set_page_slot        = api_set_page_slot;
    a.focus_page_element   = api_focus;
    a.set_page_tile_text   = api_tile_text;
    a.set_page_tile_colors = api_tile_colors;
    a.add_title_entry      = api_add_title_entry;
    a.title_started_by     = api_title_started_by;
    a.playing_online       = api_playing_online;
    a.session_id           = api_session_id;
    a.session_log_path     = api_session_log_path;
    a.request_save_snapshot = api_request_save_snapshot;
    a.add_dump_memory      = api_add_dump_memory;
    a.add_hotkey           = api_add_hotkey;
    a.add_command          = api_add_command;
    return a;
}

// Plays the game's part: flips every On/Off or choice setting, moves every slider,
// presses every hotkey and runs every command with "world". FH2DotNet's own are left
// to the script.
void exercise() {
    std::printf("[platform] --- the player changes every setting ---\n");
    for (const auto& s : g_settings) {
        if (s.mod->name == "Platform.NET") continue;
        if (s.kind == FH2_SETTING_SLIDER && s.on_slide) s.on_slide(s.user, s.key.c_str(), 0.5f);
        else if (s.on_change) s.on_change(s.user, s.key.c_str(), s.kind == FH2_SETTING_CHOICE ? 1 : 0);
    }
    std::printf("[platform] --- every hotkey is pressed ---\n");
    for (const auto& k : g_hotkeys)
        if (k.fn && k.mod->name != "Platform.NET") k.fn(k.user, k.key.c_str());
    std::printf("[platform] --- every command runs ---\n");
    for (const auto& c : g_commands) {
        if (c.mod->name == "Platform.NET") continue;
        char reply[64];   // small on purpose: replies are cut to fit
        c.fn(c.user, c.name.c_str(), "world", reply, sizeof reply);
        std::printf("[platform] %s %s world -> \"%s\"\n", nm(c.mod), c.name.c_str(), reply);
    }
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    if (argc < 3) {
        std::fprintf(stderr, "usage: fake_platform <azimuth_dir> <mod.dll>...\n");
        return 2;
    }
    setvbuf(stdout, nullptr, _IONBF, 0);
    g_azimuth           = argv[1];
    static FH2ModApi api = make_api();

    int failed = 0;
    for (int i = 2; i < argc; ++i) {
        if (std::wstring(argv[i]) == L"--") break;
        std::printf("[platform] loading %s\n", narrow(argv[i]).c_str());
        HMODULE h = LoadLibraryExW(argv[i], nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (!h) {
            std::printf("[platform] FAILED to load (error %lu)\n", GetLastError());
            ++failed;
            continue;
        }
        auto init = reinterpret_cast<FH2ModInitFn>(reinterpret_cast<void*>(GetProcAddress(h, "fh2mod_init")));
        if (!init) {
            std::printf("[platform] no fh2mod_init\n");
            ++failed;
            continue;
        }
        const int rc = init(&api);
        std::printf("[platform] fh2mod_init returned %d\n", rc);
        if (rc) ++failed;
    }

    exercise();

    // After "--": a script. "<mod> <command> [args]" runs a command; "!exercise" uses everything
    // again; "!set <mod> <key> <value>" changes a setting as the player would; "!touch <file>"
    // rewrites a file (as a build copying it in would); "!sleep <ms>" waits.
    for (int i = 2; i < argc; ++i) {
        if (std::wstring(argv[i]) != L"--") continue;
        for (int j = i + 1; j < argc; ++j) {
            const std::string line = narrow(argv[j]);
            std::printf("[platform] > %s\n", line.c_str());
            if (line == "!exercise") {
                exercise();
            } else if (line.rfind("!sleep ", 0) == 0) {
                Sleep(static_cast<DWORD>(std::atoi(line.c_str() + 7)));
            } else if (line.rfind("!touch ", 0) == 0) {
                const std::wstring f = argv[j] + 7;
                std::vector<char> bytes;
                if (FILE* in = _wfopen(f.c_str(), L"rb")) {
                    char b[65536];
                    size_t n;
                    while ((n = std::fread(b, 1, sizeof b, in)) > 0) bytes.insert(bytes.end(), b, b + n);
                    std::fclose(in);
                }
                if (FILE* out = _wfopen(f.c_str(), L"wb")) {
                    std::fwrite(bytes.data(), 1, bytes.size(), out);
                    std::fclose(out);
                }
            } else if (line.rfind("!open ", 0) == 0 || line.rfind("!select ", 0) == 0) {
                // "!open <mod> <page>" opens a page; "!select <mod> <page> <row>" presses A on a row.
                char mod[64], page[64], row[64] = "";
                const bool open = line[1] == 'o';
                if (std::sscanf(line.c_str() + (open ? 6 : 8), "%63s %63s %63s", mod, page, row) >= 2)
                    for (const auto& pg : g_pages)
                        if (pg.mod->name == mod && pg.key == page) {
                            if (open && pg.on_open) pg.on_open(pg.user, page);
                            if (!open && pg.on_select) pg.on_select(pg.user, page, row);
                        }
            } else if (line.rfind("!set ", 0) == 0) {
                char mod[64], key[64];
                int value = 0;
                if (std::sscanf(line.c_str() + 5, "%63s %63s %d", mod, key, &value) == 3)
                    for (const auto& st : g_settings)
                        if (st.mod->name == mod && st.key == key && st.on_change) st.on_change(st.user, key, value);
            } else {
                const auto sp1  = line.find(' ');
                const auto mod  = line.substr(0, sp1);
                const auto rest = sp1 == std::string::npos ? std::string() : line.substr(sp1 + 1);
                const auto sp2  = rest.find(' ');
                const auto cmd  = rest.substr(0, sp2);
                const auto args = sp2 == std::string::npos ? std::string() : rest.substr(sp2 + 1);
                bool found      = false;
                for (const auto& c : g_commands)
                    if (c.mod->name == mod && c.name == cmd) {
                        char reply[4096];
                        c.fn(c.user, c.name.c_str(), args.c_str(), reply, sizeof reply);
                        std::printf("[platform] %s\n", reply);
                        found = true;
                        break;
                    }
                if (!found) std::printf("[platform] no command %s %s\n", mod.c_str(), cmd.c_str());
            }
        }
        break;
    }
    Sleep(500);   // let the mods' own threads log
    std::printf("[platform] done: %d failed\n", failed);
    return failed ? 1 : 0;
}
