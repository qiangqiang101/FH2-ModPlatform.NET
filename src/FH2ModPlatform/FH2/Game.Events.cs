using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using FH2ModPlatform;

namespace FH2;

public static unsafe partial class Game
{
    private static readonly List<Action<long>> s_rewound = new();
    private static readonly List<Action> s_relocated = new();
    private static long s_lastPlayerFrame;

    /// <summary>
    /// True while you're driving: your car is being simulated (not paused, not loading) and the HUD speedometer
    /// is up (not in the garage, a menu, a race intro...). Any thread.
    /// </summary>
    public static bool IsDriving =>
        IsReady && Environment.TickCount64 - Interlocked.Read(ref s_lastPlayerFrame) < 200 && Hud.IsSpeedometerShown;

    /// <summary>
    /// You rewound: your car is back where it was at the moment given (an <see cref="Environment.TickCount64"/>
    /// value), so undo what happened since. On the game's thread, when the car drives on.
    /// </summary>
    public static event Action<long> Rewound
    {
        add { Start(); lock (s_lock) s_rewound.Add(value); }
        remove { lock (s_lock) s_rewound.Remove(value); }
    }

    /// <summary>
    /// Your car was put somewhere new: a race started, you quit one, changed car or fast travelled. On the game's
    /// thread, when the car drives on.
    /// </summary>
    public static event Action CarRelocated
    {
        add { Start(); lock (s_lock) s_relocated.Add(value); }
        remove { lock (s_lock) s_relocated.Remove(value); }
    }

    private static void ForgetEvents(Assembly assembly)
    {
        lock (s_lock)
        {
            s_rewound.RemoveAll(h => h.Method.DeclaringType?.Assembly == assembly);
            s_relocated.RemoveAll(h => h.Method.DeclaringType?.Assembly == assembly);
        }
    }

    // ---- Telling a rewind from a relocation (the way FnFNitro does) -------------------------------------
    // Starting or leaving a race, changing car, fast travelling and rewinding all hide the speedometer while the car
    // keeps simulating (a pause doesn't: the car stops with it). Arm when the speedometer has been off for 2 s with
    // the car running, or when the car resumes with it off; decide when it comes back. A rewind replays the car's
    // path backwards, ending 40 m or more from where the speedometer went off, on a spot it passed a few seconds
    // earlier; anything else is a relocation. The path is recorded while the speedometer is up.
    private readonly record struct PathPoint(Vector3 Position, long Tick);
    private static readonly List<PathPoint> s_path = new();
    private static Vector3 s_freezePos;
    private static bool s_prevSpeedo, s_armed;
    private static long s_offSince;

    // From OnCarUpdate, for the player's car, before the Tick handlers.
    private static void PlayerFrame(nint car)
    {
        long now = Environment.TickCount64;
        bool resumed = now - Interlocked.Exchange(ref s_lastPlayerFrame, now) > 200;
        if (!Hud.IsReady) return;
        bool speedo = Hud.IsSpeedometerShown;
        var body = *(nint*)(car + Addresses.CarBodyField);
        if (body == 0) return;
        var p = (float*)(body + Addresses.BodyToDynamics + Addresses.DynamicsPosition);
        var pos = new Vector3(p[0], p[1], p[2]);

        if (speedo && !s_armed && (s_path.Count == 0 || now - s_path[^1].Tick >= 100))
        {
            s_path.Add(new PathPoint(pos, now));
            if (s_path.Count > 600) s_path.RemoveAt(0);   // a minute at 10 a second
        }
        if (s_prevSpeedo && !speedo) s_freezePos = pos;
        if (!speedo)
        {
            if (s_offSince == 0) s_offSince = now;
            if (resumed || now - s_offSince >= 2000) s_armed = true;
        }
        else s_offSince = 0;
        s_prevSpeedo = speedo;

        if (!speedo || !s_armed) return;
        s_armed = false;
        int best = -1;
        float bestD = 25;
        if (Vector3.Distance(pos, s_freezePos) > 40)
        {
            for (int i = 0; i < s_path.Count; i++)
            {
                float d = Vector3.Distance(pos, s_path[i].Position);
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }
        }
        if (best >= 0)
        {
            long then = s_path[best].Tick;
            s_path.RemoveRange(best + 1, s_path.Count - best - 1);   // what came after it was undone
            Action<long>[] hs;
            lock (s_lock) hs = s_rewound.ToArray();
            foreach (var h in hs) Safe(() => h(then), "Game.Rewound");
        }
        else
        {
            s_path.Clear();   // somewhere new: the old path doesn't lead here
            Action[] hs;
            lock (s_lock) hs = s_relocated.ToArray();
            foreach (var h in hs) Safe(h, "Game.CarRelocated");
        }
    }

    // ---- The language ------------------------------------------------------------------------------

    /// <summary>
    /// The game's text language, as its code: "EN", "DE", "FR", "ES", "MX", "IT", "BR", "JP", "KO", "CHT", "PL",
    /// "RU", "NL" (and "GB", "CZ", "HU"... with English text). Null until the game has loaded its text. Any thread.
    /// </summary>
    public static string? Language
    {
        get
        {
            if (!IsReady) return null;
            var manager = *(nint*)(s_base + Addresses.StringsManagerGlobal);
            if (manager == 0) return null;
            var name = (byte*)(manager + Addresses.LanguageCodeField);
            // An MSVC std::string kept in place (capacity 15), as codes are: the text, then its length at +16.
            long count = *(long*)(name + 16), capacity = *(long*)(name + 24);
            if (capacity != 15 || count is < 2 or > 3 || name[count] != 0) return null;
            // The game keeps it in lowercase ("en", "cht"): any letters, given back in capitals.
            for (int i = 0; i < count; i++)
                if (!char.IsAsciiLetter((char)name[i])) return null;
            return Marshal.PtrToStringAnsi((nint)name, (int)count)!.ToUpperInvariant();
        }
    }

    /// <summary>
    /// The language Azimuth starts the game in (its data\azimuth.ini), as a <see cref="Language"/> code: what the
    /// game will most likely show before it has loaded its text. "EN" if it can't be told. Any thread.
    /// </summary>
    public static string LaunchLanguage
    {
        get
        {
            try
            {
                var ini = Path.Combine(ModPlatform.AzimuthDir, "data", "azimuth.ini");
                var title = IniValue(ini, "launcher", "game");
                var locale = title is null ? null : IniValue(ini, "title-" + title, "language");
                if (locale is null) return "EN";
                if (locale.Equals("system", StringComparison.OrdinalIgnoreCase))
                    locale = System.Globalization.CultureInfo.InstalledUICulture.Name;
                return LanguageOf(locale);
            }
            catch
            {
                return "EN";
            }
        }
    }

    // A locale ("ja-JP", "pt-BR"...) as the game's text language. Czech and Hungarian: the game's text is English.
    private static string LanguageOf(string locale)
    {
        var parts = locale.Split('-', '_');
        string lang = parts[0].ToLowerInvariant(), region = parts.Length > 1 ? parts[^1].ToUpperInvariant() : "";
        return lang switch
        {
            "pt" => "BR",
            "es" => region is "ES" or "" ? "ES" : "MX",
            "zh" => region is "TW" or "HK" or "MO" or "HANT" || locale.Contains("Hant", StringComparison.OrdinalIgnoreCase) ? "CHT" : "EN",
            "ja" => "JP",
            "ko" => "KO",
            "de" => "DE",
            "fr" => "FR",
            "it" => "IT",
            "pl" => "PL",
            "ru" => "RU",
            "nl" => "NL",
            _ => "EN",
        };
    }

    private static string? IniValue(string path, string section, string key)
    {
        if (!File.Exists(path)) return null;
        bool inSection = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inSection = line.Equals($"[{section}]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            int eq = line.IndexOf('=');
            if (inSection && eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line[(eq + 1)..].Trim();
        }
        return null;
    }
}
