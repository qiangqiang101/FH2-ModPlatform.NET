// FH2Console.exe: a console window for the FH2 Mod Platform, beside the game.
//
//   FH2Console.exe [--pid <game process id>] [--log <platform.log>] [--key <virtual-key code>]
//
// FH2DotNet.dll starts it when its "Console window" switch is on, but it runs
// on its own just as well (from fh2-mods\dotnet, or anywhere with --log). It
//   - shows fh2-mods\platform.log as it's written: every mod's lines, the
//     platform's, and .NET mods' Console/Debug output, with failures in red;
//   - runs what you type as a command through the platform's pipe
//     (\\.\pipe\fh2mod-<pid>): "list", "<mod> <command> [args]", plus
//     "reload [mod]" and "mods" for hosted .NET mods;
//   - closes itself when the game does.
// It's a process of its own because closing a console window ends the process
// the console belongs to, and that mustn't be the game.
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cwctype>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace {

std::mutex g_out;
HANDLE g_con = nullptr;
std::atomic<DWORD> g_pid{0};
WORD g_key = 0;   // the console key (a virtual-key code): closes this window too

enum Color : WORD {
    kNormal = FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_BLUE,
    kDim    = FOREGROUND_INTENSITY,
    kRed    = FOREGROUND_RED | FOREGROUND_INTENSITY,
    kYellow = FOREGROUND_RED | FOREGROUND_GREEN | FOREGROUND_INTENSITY,
    kCyan   = FOREGROUND_GREEN | FOREGROUND_BLUE | FOREGROUND_INTENSITY,
    kGreen  = FOREGROUND_GREEN | FOREGROUND_INTENSITY,
};

std::wstring widen(const std::string& s) {
    if (s.empty()) return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring w(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
    return w;
}

std::string narrow(const std::wstring& w) {
    if (w.empty()) return {};
    const int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), nullptr, 0, nullptr, nullptr);
    std::string s(static_cast<size_t>(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), s.data(), n, nullptr, nullptr);
    return s;
}

// What's being typed at the prompt. Log lines that arrive meanwhile go above it: the
// prompt line is wiped, the text written, and the prompt drawn again under it.
std::wstring g_typing;
bool g_prompt = false;

void write_raw(const std::wstring& text, WORD color = kNormal) {
    SetConsoleTextAttribute(g_con, color);
    DWORD n = 0;
    WriteConsoleW(g_con, text.data(), static_cast<DWORD>(text.size()), &n, nullptr);
    SetConsoleTextAttribute(g_con, kNormal);
}

void wipe_prompt() {
    CONSOLE_SCREEN_BUFFER_INFO info{};
    GetConsoleScreenBufferInfo(g_con, &info);
    // The prompt can wrap: clear from where it started.
    const int width = info.dwSize.X > 0 ? info.dwSize.X : 80;
    const int rows  = static_cast<int>((g_typing.size() + 2) / static_cast<size_t>(width));
    COORD start{0, static_cast<SHORT>(std::max(0, info.dwCursorPosition.Y - rows))};
    DWORD written = 0;
    FillConsoleOutputCharacterW(g_con, L' ', static_cast<DWORD>(width * (rows + 1)), start, &written);
    SetConsoleCursorPosition(g_con, start);
}

void draw_prompt() { write_raw(L"> " + g_typing, kCyan); }

void print(const std::wstring& text, WORD color = kNormal) {
    std::scoped_lock lk{g_out};
    if (g_prompt) wipe_prompt();
    write_raw(text, color);
    if (g_prompt) draw_prompt();
}

bool contains_ci(const std::wstring& hay, const wchar_t* needle) {
    std::wstring h = hay, n = needle;
    for (auto& c : h) c = static_cast<wchar_t>(towlower(c));
    for (auto& c : n) c = static_cast<wchar_t>(towlower(c));
    return h.find(n) != std::wstring::npos;
}

// A log line, coloured by what it says.
void print_log_line(const std::string& utf8) {
    const std::wstring line = widen(utf8);
    WORD c = kNormal;
    if (contains_ci(line, L"failed") || contains_ci(line, L"crash") || contains_ci(line, L"exception") ||
        contains_ci(line, L"error"))
        c = kRed;
    else if (contains_ci(line, L"refuse") || contains_ci(line, L"warning") || contains_ci(line, L"doesn't") ||
             contains_ci(line, L"isn't"))
        c = kYellow;
    else if (line.find(L"] console") != std::wstring::npos || line.find(L"] trace: ") != std::wstring::npos)
        c = kGreen;
    print(line + L"\n", c);
}

// Follows the log: the last lines first, then whatever's added. A new session
// truncates it (the platform opens it afresh), which starts over.
void tail(std::wstring path) {
    HANDLE f = INVALID_HANDLE_VALUE;
    LONGLONG pos = 0;
    bool first   = true;
    std::string partial;
    for (;;) {
        if (f == INVALID_HANDLE_VALUE) {
            f = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                            OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (f == INVALID_HANDLE_VALUE) {
                if (first) {
                    print(L"waiting for " + path + L"\n", kDim);
                    first = false;
                }
                Sleep(500);
                continue;
            }
            LARGE_INTEGER size{};
            GetFileSizeEx(f, &size);
            pos = 0;
            if (size.QuadPart > 0) {
                // Start about 40 lines from the end.
                const LONGLONG back = std::min<LONGLONG>(size.QuadPart, 8192);
                std::string tailbuf(static_cast<size_t>(back), '\0');
                LARGE_INTEGER at{};
                at.QuadPart = size.QuadPart - back;
                SetFilePointerEx(f, at, nullptr, FILE_BEGIN);
                DWORD got = 0;
                ReadFile(f, tailbuf.data(), static_cast<DWORD>(back), &got, nullptr);
                tailbuf.resize(got);
                size_t start = 0, count = 0;
                for (size_t i = tailbuf.size(); i-- > 0;)
                    if (tailbuf[i] == '\n' && ++count > 40) {
                        start = i + 1;
                        break;
                    }
                if (start == 0 && at.QuadPart > 0) start = tailbuf.find('\n') + 1;
                partial = tailbuf.substr(start);
                pos     = size.QuadPart;
                size_t nl;
                while ((nl = partial.find('\n')) != std::string::npos) {
                    std::string l = partial.substr(0, nl);
                    if (!l.empty() && l.back() == '\r') l.pop_back();
                    print_log_line(l);
                    partial.erase(0, nl + 1);
                }
            }
        }
        LARGE_INTEGER size{};
        if (!GetFileSizeEx(f, &size)) {
            CloseHandle(f);
            f = INVALID_HANDLE_VALUE;
            continue;
        }
        if (size.QuadPart < pos) {
            print(L"--- platform.log started over (a new session) ---\n", kCyan);
            pos = 0;
            partial.clear();
        }
        if (size.QuadPart > pos) {
            LARGE_INTEGER at{};
            at.QuadPart = pos;
            SetFilePointerEx(f, at, nullptr, FILE_BEGIN);
            char buf[16384];
            DWORD got = 0;
            if (ReadFile(f, buf, sizeof buf, &got, nullptr) && got) {
                pos += got;
                partial.append(buf, got);
                size_t nl;
                while ((nl = partial.find('\n')) != std::string::npos) {
                    std::string l = partial.substr(0, nl);
                    if (!l.empty() && l.back() == '\r') l.pop_back();
                    print_log_line(l);
                    partial.erase(0, nl + 1);
                }
                continue;   // more may be waiting
            }
        }
        Sleep(100);
    }
}

// The platform's command pipes, one per running game: fh2mod-<pid>.
std::vector<DWORD> game_pids() {
    std::vector<DWORD> pids;
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW(L"\\\\.\\pipe\\*", &fd);
    if (h == INVALID_HANDLE_VALUE) return pids;
    do {
        const std::wstring n = fd.cFileName;
        if (n.rfind(L"fh2mod-", 0) == 0) pids.push_back(static_cast<DWORD>(std::wcstoul(n.c_str() + 7, nullptr, 10)));
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    return pids;
}

// One request on the pipe, one reply (see the platform's commands.cpp).
bool request(const std::string& line, std::string& reply) {
    DWORD pid = g_pid;
    if (!pid) {
        const auto pids = game_pids();
        if (pids.empty()) {
            reply = "the game isn't running, or no mod has added a command yet";
            return false;
        }
        pid = pids[0];
    }
    const std::wstring name = L"\\\\.\\pipe\\fh2mod-" + std::to_wstring(pid);
    HANDLE p                = INVALID_HANDLE_VALUE;
    for (int i = 0; i < 25; ++i) {
        p = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (p != INVALID_HANDLE_VALUE || GetLastError() != ERROR_PIPE_BUSY) break;
        WaitNamedPipeW(name.c_str(), 200);
    }
    if (p == INVALID_HANDLE_VALUE) {
        reply = "can't reach the game's command pipe (error " + std::to_string(GetLastError()) + ")";
        return false;
    }
    DWORD mode = PIPE_READMODE_MESSAGE;
    SetNamedPipeHandleState(p, &mode, nullptr, nullptr);
    DWORD w = 0;
    if (!WriteFile(p, line.data(), static_cast<DWORD>(line.size()), &w, nullptr)) {
        CloseHandle(p);
        reply = "couldn't send the command";
        return false;
    }
    reply.clear();
    char buf[4096];
    for (;;) {
        DWORD n      = 0;
        const BOOL ok = ReadFile(p, buf, sizeof buf, &n, nullptr);
        if (n) reply.append(buf, n);
        if (ok || GetLastError() != ERROR_MORE_DATA) {
            if (!ok || n == 0) break;
        }
    }
    CloseHandle(p);
    return true;
}

void help() {
    print(L"Type a command for the game's mods:\n"
          L"  list                      every mod's commands\n"
          L"  <mod> <command> [args]    run one, like: HelloMod.NET hello you\n"
          L"  reload [mod]              start a rebuilt hosted .NET mod again (all of them without a name)\n"
          L"  mods                      the hosted .NET mods and what they're doing\n"
          L"  cls                       clear the window\n"
          L"  help                      this\n"
          L"Up and Down go through what you typed; the console key (~ unless you changed it) closes this window.\n",
          kCyan);
}

BOOL WINAPI on_ctrl(DWORD type) {
    // Ctrl+C would end this window; it's for copying here.
    return type == CTRL_C_EVENT || type == CTRL_BREAK_EVENT;
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    std::wstring log_path;
    for (int i = 1; i + 1 < argc; ++i) {
        const std::wstring a = argv[i];
        if (a == L"--pid") g_pid = static_cast<DWORD>(std::wcstoul(argv[++i], nullptr, 10));
        else if (a == L"--log") log_path = argv[++i];
        else if (a == L"--key") g_key = static_cast<WORD>(std::wcstoul(argv[++i], nullptr, 10));
    }
    if (log_path.empty()) {
        // fh2-mods\dotnet\FH2Console.exe -> fh2-mods\platform.log
        wchar_t self[MAX_PATH * 2];
        const DWORD n = GetModuleFileNameW(nullptr, self, static_cast<DWORD>(std::size(self)));
        std::wstring dir(self, n);
        dir.resize(dir.find_last_of(L"\\/"));
        dir.resize(dir.find_last_of(L"\\/") == std::wstring::npos ? 0 : dir.find_last_of(L"\\/"));
        log_path = dir + L"\\platform.log";
    }

    g_con = GetStdHandle(STD_OUTPUT_HANDLE);
    SetConsoleCtrlHandler(on_ctrl, TRUE);
    SetConsoleCP(CP_UTF8);
    SetConsoleOutputCP(CP_UTF8);
    // Selecting text in the window would pause every write to it: keep QuickEdit off.
    HANDLE in = GetStdHandle(STD_INPUT_HANDLE);
    DWORD mode = 0;
    if (GetConsoleMode(in, &mode)) SetConsoleMode(in, (mode | ENABLE_EXTENDED_FLAGS) & ~ENABLE_QUICK_EDIT_MODE);
    SetConsoleTitleW((L"FH2 Mod Platform console" + (g_pid ? L" (game " + std::to_wstring(g_pid) + L")" : L"")).c_str());

    print(L"FH2 Mod Platform console: " + log_path + L"\n", kCyan);
    help();
    std::thread(tail, log_path).detach();

    if (g_pid) {
        std::thread([] {
            HANDLE game = OpenProcess(SYNCHRONIZE, FALSE, g_pid);
            if (!game) return;
            WaitForSingleObject(game, INFINITE);
            CloseHandle(game);
            print(L"\nThe game has closed; this window closes in 5 seconds.\n", kCyan);
            Sleep(5000);
            ExitProcess(0);
        }).detach();
    }

    // Keys one at a time (not whole lines), so the console key can close the window.
    if (GetConsoleMode(in, &mode))
        SetConsoleMode(in, (mode | ENABLE_EXTENDED_FLAGS | ENABLE_WINDOW_INPUT) &
                               ~(ENABLE_QUICK_EDIT_MODE | ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT | ENABLE_PROCESSED_INPUT));
    std::vector<std::wstring> history;
    for (;;) {
        {
            std::scoped_lock lk{g_out};
            g_typing.clear();
            g_prompt = true;
            draw_prompt();
        }
        size_t back = history.size();   // position in the history: Up/Down
        std::wstring line;
        for (bool done = false; !done;) {
            INPUT_RECORD rec;
            DWORD n = 0;
            if (!ReadConsoleInputW(in, &rec, 1, &n) || n == 0) {
                Sleep(50);
                continue;
            }
            if (rec.EventType != KEY_EVENT || !rec.Event.KeyEvent.bKeyDown) continue;
            const WORD vk     = rec.Event.KeyEvent.wVirtualKeyCode;
            const wchar_t ch  = rec.Event.KeyEvent.uChar.UnicodeChar;
            const DWORD state = rec.Event.KeyEvent.dwControlKeyState;
            std::scoped_lock lk{g_out};
            auto set_typing = [&](const std::wstring& t) {
                wipe_prompt();
                g_typing = t;
                draw_prompt();
            };
            if (g_key && vk == g_key) {
                ExitProcess(0);   // the console key: FH2DotNet.dll sees it close and flips its switch
            } else if (vk == VK_RETURN) {
                line     = g_typing;
                g_prompt = false;
                write_raw(L"\n");
                done = true;
            } else if (vk == VK_BACK) {
                if (!g_typing.empty()) set_typing(g_typing.substr(0, g_typing.size() - 1));
            } else if (vk == VK_ESCAPE) {
                set_typing(L"");
            } else if (vk == VK_UP) {
                if (back > 0) set_typing(history[--back]);
            } else if (vk == VK_DOWN) {
                if (back + 1 < history.size()) set_typing(history[++back]);
                else {
                    back = history.size();
                    set_typing(L"");
                }
            } else if ((state & (LEFT_CTRL_PRESSED | RIGHT_CTRL_PRESSED)) && (vk == 'C' || vk == 'D')) {
                set_typing(L"");   // never "end the program" here
            } else if (ch >= 32) {
                g_typing += ch;
                write_raw(std::wstring(1, ch), kCyan);
            }
        }
        while (!line.empty() && line.back() == L' ') line.pop_back();
        while (!line.empty() && line.front() == L' ') line.erase(0, 1);
        if (line.empty()) continue;
        if (history.empty() || history.back() != line) history.push_back(line);

        std::wstring lower = line;
        for (auto& c : lower) c = static_cast<wchar_t>(towlower(c));
        if (lower == L"help" || lower == L"?") {
            help();
            continue;
        }
        if (lower == L"cls" || lower == L"clear") {
            std::scoped_lock lk{g_out};
            CONSOLE_SCREEN_BUFFER_INFO info{};
            GetConsoleScreenBufferInfo(g_con, &info);
            DWORD written = 0;
            const DWORD cells = static_cast<DWORD>(info.dwSize.X) * static_cast<DWORD>(info.dwSize.Y);
            FillConsoleOutputCharacterW(g_con, L' ', cells, {0, 0}, &written);
            FillConsoleOutputAttribute(g_con, info.wAttributes, cells, {0, 0}, &written);
            SetConsoleCursorPosition(g_con, {0, 0});
            continue;
        }
        // Shortcuts for FH2DotNet's own commands.
        if (lower == L"reload" || lower.rfind(L"reload ", 0) == 0 || lower == L"mods") line = L"Platform.NET " + line;

        std::string reply;
        const bool ok = request(narrow(line), reply);
        while (!reply.empty() && (reply.back() == '\n' || reply.back() == '\r')) reply.pop_back();
        const bool err = !ok || reply.rfind("error:", 0) == 0;
        print(widen(reply) + L"\n", err ? kRed : kCyan);
    }
}
