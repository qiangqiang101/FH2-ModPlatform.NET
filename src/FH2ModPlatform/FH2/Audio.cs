using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FH2ModPlatform;
using FH2ModPlatform.Interop;
using FH2ModPlatform.Native;

namespace FH2;

/// <summary>
/// Sounds of your own, played through the game's audio: heard from where they are (a car, a spot in
/// the world), so they get distance and direction like the game's own. Load a sound once
/// (<see cref="LoadSound(string, bool)"/>), play it as often as you like. Any thread.
/// </summary>
/// <example>
/// <code>
/// Dim boost = Audio.LoadSound(Path.Combine(ModFolder, "boost.wav"), looped:=True)
/// Dim playing = boost.PlayOn(Player.Car, volume:=0.5F)
/// ...
/// playing.Stop()
/// </code>
/// </example>
/// <remarks>
/// WAV files: 16-bit ones are made mono at the rate the game mixes at (it places them in 3D itself);
/// anything else goes to the game's FMOD as it is. The game's own volume sliders don't reach these sounds.
/// </remarks>
public static unsafe class Audio
{
    private static readonly ConcurrentDictionary<(string, bool), Sound> s_sounds = new();
    private static readonly List<SoundChannel> s_channels = new();
    private static readonly ConcurrentDictionary<nint, CarSpot> s_spots = new();
    private static void** s_updateOriginal;
    private static volatile bool s_ready;
    private static nint s_base, s_core;
    private static int s_mixRate;
    private static long s_lastTick;
    private static bool s_loggedFailure;

    /// <summary>True once the game's audio can be used (the game's code is as expected).</summary>
    public static bool IsReady => Game.IsReady && s_ready;

    /// <summary>A sound from a file (a .wav). The same file loaded again is the same sound.</summary>
    public static Sound LoadSound(string path, bool looped = false) => LoadSound(File.ReadAllBytes(path), looped);

    /// <summary>A sound from a whole sound file in memory (a .wav).</summary>
    public static Sound LoadSound(byte[] file, bool looped = false)
    {
        // The same sound, from any mod or a reloaded copy, is made once (the game's audio keeps it for the session).
        var key = (Convert.ToHexString(SHA256.HashData(file)), looped);
        return s_sounds.GetOrAdd(key, _ => new Sound(file, looped));
    }

    /// <summary>
    /// One sound made of pieces (16-bit WAVs at one rate): the <paramref name="bed"/> sounds played one after
    /// another, taking turns, each crossfading into the next, for <paramref name="seconds"/>, with
    /// <paramref name="intro"/> (if any) over its start. For a long sound that doesn't sound like a loop, like an
    /// engine or a hiss. Null if the pieces aren't 16-bit WAVs of one rate.
    /// </summary>
    public static Sound? MixSound(byte[]? intro, byte[][] bed, float seconds, bool looped = false, float crossfadeSeconds = 0.02f)
    {
        if (bed.Length == 0 || seconds <= 0) return null;
        var pieces = bed.Select(Wav.Stereo).ToList();
        var first = intro is null ? null : Wav.Stereo(intro);
        if (pieces.Any(p => p is null) || (intro is not null && first is null)) return null;
        int rate = pieces[0]!.Value.Rate;
        if (pieces.Any(p => p!.Value.Rate != rate) || (first is { } f0 && f0.Rate != rate)) return null;
        int fade = Math.Max(1, (int)(rate * crossfadeSeconds));
        if (pieces.Any(p => p!.Value.Samples.Length / 2 <= 2 * fade)) return null;

        int frames = (int)(seconds * rate);
        var mix = new float[frames * 2];
        for (int pos = 0, n = 0; pos < frames; n++)
        {
            var s = pieces[n % pieces.Count]!.Value.Samples;
            int len = s.Length / 2;
            for (int j = 0; j < len && pos + j < frames; j++)
            {
                float keep = 0, add = 1;
                if (n > 0 && j < fade)
                {
                    // An equal-power crossfade from the piece before.
                    float a = (j + 0.5f) / fade * MathF.PI / 2;
                    keep = MathF.Cos(a);
                    add = MathF.Sin(a);
                }
                for (int c = 0; c < 2; c++)
                {
                    ref float o = ref mix[(pos + j) * 2 + c];
                    o = o * keep + s[j * 2 + c] * add;
                }
            }
            pos += len - fade;
        }
        if (first is { } f)
            for (int i = 0; i < f.Samples.Length && i < mix.Length; i++) mix[i] += f.Samples[i];
        var wav = Wav.Write(mix, 2, rate, out float gain);
        var key = (Convert.ToHexString(SHA256.HashData(wav)), looped);
        return s_sounds.GetOrAdd(key, _ => new Sound(wav, looped, gain));
    }

    /// <summary>Stops every sound playing (fading out).</summary>
    public static void StopAll()
    {
        lock (s_channels)
            foreach (var c in s_channels) c.Stop();
    }

    internal static SoundChannel Start(SoundChannel channel)
    {
        Game.RequireBase("Audio");
        if (!s_ready) throw new InvalidOperationException("Sound isn't available (a different game build; platform.log says).");
        lock (s_channels) s_channels.Add(channel);
        return channel;
    }

    internal static Assembly? Caller(Assembly calling) => ModPlatform.IsHosted ? calling : null;

    /// <summary>A hosted mod is being replaced: the sounds it plays stop.</summary>
    internal static void Forget(Assembly assembly)
    {
        lock (s_channels)
            foreach (var c in s_channels)
                if (c.Owner == assembly) c.Stop();
    }

    // ---- Installing ---------------------------------------------------------------------------------

    internal static void Install(nint b, FH2Mod* owner)
    {
        if (Game.Mismatch(b, Addresses.Sound) is { } bad)
        {
            Game.Log(owner, $"Audio off: the game's code isn't as expected at {bad}");
            return;
        }
        s_base = b;
        s_updateOriginal = (void**)NativeMemory.AllocZeroed((nuint)sizeof(void*));
        using var u = new Utf8Scope();
        var hook = ModPlatform.Api->HookFunction(owner, u.Str("FH2 audio (FMOD Studio::System::update)"),
            (void*)(b + Addresses.FmodStudioUpdate.Offset), (delegate* unmanaged<nint, int>)&OnStudioUpdate, s_updateOriginal);
        if (hook is null || ModPlatform.Api->EnableHooks(owner, hook) == 0)
        {
            Game.Log(owner, "Audio off: couldn't hook the game's audio update");
            return;
        }
        s_ready = true;
    }

    // ---- Where the cars are, for sounds on them (from the cars' update, on the game's thread) ----------

    private sealed class CarSpot
    {
        public Vector3 Position;
        public long Seen, Wanted;
    }

    internal static void NoteCar(nint car)
    {
        if (s_spots.IsEmpty || !s_spots.TryGetValue(car, out var spot)) return;
        long now = Environment.TickCount64;
        if (now - spot.Wanted > 1000)
        {
            s_spots.TryRemove(car, out _);
            return;
        }
        var body = *(nint*)(car + Addresses.CarBodyField);
        if (body == 0) return;
        var p = (float*)(body + Addresses.BodyToDynamics + Addresses.DynamicsPosition);
        var pos = new Vector3(p[0], p[1], p[2]);
        // The car as drawn moves in step with the listener; the physics runs on its own clock, and the difference
        // would jitter the sound's volume and panning. The physics position if the drawn one is far off.
        var pres = Game.Presentation(car);
        if (pres != 0)
        {
            var q = (float*)(pres + Addresses.PresentationPosition);
            var drawn = new Vector3(q[0], q[1], q[2]);
            if (float.IsFinite(drawn.X + drawn.Y + drawn.Z) && Vector3.DistanceSquared(drawn, pos) < 20 * 20) pos = drawn;
        }
        spot.Position = pos;
        Volatile.Write(ref spot.Seen, now);
    }

    // ---- The game's audio thread ------------------------------------------------------------------

    [UnmanagedCallersOnly]
    private static int OnStudioUpdate(nint studio)
    {
        int r = ((delegate* unmanaged<nint, int>)*s_updateOriginal)(studio);
        try
        {
            Tick(studio);
        }
        catch (Exception e)
        {
            if (!s_loggedFailure)
            {
                s_loggedFailure = true;
                Game.Log(ModPlatform.GameOwner, "Audio: " + e);
            }
        }
        return r;
    }

    private static nint F(Addresses.Code c) => s_base + c.Offset;

    private static void Tick(nint studio)
    {
        long now = Environment.TickCount64;
        float step = Math.Min(0.1f, (now - s_lastTick) * 0.001f) / SoundChannel.FadeSeconds;
        s_lastTick = now;
        if (s_core == 0)
        {
            nint core;
            if (((delegate* unmanaged<nint, nint*, int>)F(Addresses.FmodGetLowLevel))(studio, &core) != 0 || core == 0) return;
            int rate = 0, mode = 0, raw = 0;
            if (((delegate* unmanaged<nint, int*, int*, int*, int>)F(Addresses.FmodGetSoftwareFormat))(core, &rate, &mode, &raw) == 0)
                s_mixRate = rate;
            s_core = core;
        }

        SoundChannel[] channels;
        lock (s_channels)
        {
            if (s_channels.Count == 0) return;
            channels = s_channels.ToArray();
        }

        FmodVec lpos, lvel, lfwd, lup;
        bool listener = ((delegate* unmanaged<nint, int, FmodVec*, FmodVec*, FmodVec*, FmodVec*, int>)F(Addresses.FmodGetListener))(
            s_core, 0, &lpos, &lvel, &lfwd, &lup) == 0;

        foreach (var ch in channels)
        {
            Update(ch, now, step, listener, lpos, lvel);
            if (ch.Done)
                lock (s_channels) s_channels.Remove(ch);
        }
    }

    private static void Update(SoundChannel ch, long now, float step, bool listener, FmodVec lpos, FmodVec lvel)
    {
        var sound = ch.Sound;
        if (!sound.Ready(s_mixRate))
        {
            if (sound.Failed || ch.StopWanted) ch.Done = true;
            return;
        }
        if (sound.Native == 0 && !Create(sound))
        {
            ch.Done = true;
            return;
        }

        // Where it is
        FmodVec pos = lpos;
        if (ch.Car != 0)
        {
            var spot = s_spots.GetOrAdd(ch.Car, _ => new CarSpot());
            spot.Wanted = now;
            long seen = Volatile.Read(ref spot.Seen);
            if (seen == 0 || now - seen > 400)
            {
                // Not seen yet (give it a moment), or the car has gone (or the game is paused): fade out.
                if (seen != 0 || now - ch.Created > 1000) ch.StopWanted = true;
                if (ch.Native == 0)
                {
                    if (ch.StopWanted) ch.Done = true;
                    return;
                }
            }
            else pos = FmodVec.From(spot.Position);
        }
        else if (ch.At is { } at) pos = FmodVec.From(at);
        else if (!listener) return;

        if (ch.Native == 0)
        {
            if (ch.StopWanted)
            {
                ch.Done = true;
                return;
            }
            nint channel;
            if (((delegate* unmanaged<nint, nint, nint, byte, nint*, int>)F(Addresses.FmodPlaySound))(s_core, sound.Native, 0, 1, &channel) != 0 || channel == 0)
            {
                ch.Done = true;
                return;
            }
            // At the top priority: with the game's own sounds about, FMOD runs short of real voices and would keep
            // switching ours off and on (heard as crackling). Volume ramping makes volume changes smooth.
            ((delegate* unmanaged<nint, int, int>)F(Addresses.FmodSetPriority))(channel, 0);
            ((delegate* unmanaged<nint, byte, int>)F(Addresses.FmodSetVolumeRamp))(channel, 1);
            Place(channel, ch, pos, lvel, 1);
            ((delegate* unmanaged<nint, byte, int>)F(Addresses.FmodSetPaused))(channel, 0);
            ch.Native = channel;
            ch.Gain = 1;
            ch.Started = now;
            return;
        }

        ch.Gain = ch.StopWanted ? Math.Max(0, ch.Gain - step) : 1;
        bool ended = !sound.Looped && now - ch.Started > sound.Seconds * 1000 + 200;
        if ((ch.StopWanted && ch.Gain <= 0) || ended)
        {
            ((delegate* unmanaged<nint, int>)F(Addresses.FmodStop))(ch.Native);   // a channel FMOD has finished with just says so
            ch.Done = true;
            return;
        }
        Place(ch.Native, ch, pos, lvel, ch.Gain);
    }

    // Where it's heard from, how far, how loud. Given the listener's velocity: no Doppler shift.
    private static void Place(nint channel, SoundChannel ch, FmodVec pos, FmodVec lvel, float gain)
    {
        ((delegate* unmanaged<nint, FmodVec*, FmodVec*, FmodVec*, int>)F(Addresses.FmodSet3D))(channel, &pos, &lvel, null);
        ((delegate* unmanaged<nint, float, float, int>)F(Addresses.FmodSet3DMinMax))(channel, ch.FullVolumeWithin, Math.Max(ch.FullVolumeWithin + 1, ch.SilentBeyond));
        ((delegate* unmanaged<nint, float, int>)F(Addresses.FmodSetVolume))(channel, Math.Clamp(ch.Volume, 0, 1) * gain * ch.Sound.Gain);
    }

    private static bool Create(Sound sound)
    {
        var info = stackalloc byte[Addresses.FmodExInfoSize];
        new Span<byte>(info, Addresses.FmodExInfoSize).Clear();
        var data = sound.Prepared!;
        *(int*)info = Addresses.FmodExInfoSize;
        *(int*)(info + 4) = data.Length;
        uint mode = (sound.Looped ? Addresses.FmodLoopNormal : Addresses.FmodLoopOff) | Addresses.Fmod3D | Addresses.FmodCreateSample |
                    Addresses.FmodOpenMemory | Addresses.Fmod3DLinearSquareRolloff;
        nint native;
        int r;
        fixed (byte* d = data)   // FMOD copies it
            r = ((delegate* unmanaged<nint, byte*, uint, byte*, nint*, int>)F(Addresses.FmodCreateSound))(s_core, d, mode, info, &native);
        if (r != 0 || native == 0)
        {
            sound.Failed = true;
            Game.Log(ModPlatform.GameOwner, $"Audio: the game's audio couldn't make a sound of a file (FMOD error {r})");
            return false;
        }
        sound.Native = native;
        sound.Prepared = null;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FmodVec
    {
        public float X, Y, Z;
        public static FmodVec From(Vector3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };
    }
}

/// <summary>A sound to play (<see cref="Audio.LoadSound(string, bool)"/>). Play it as often as you like, at once too.</summary>
public sealed class Sound
{
    private readonly byte[] _file;
    private int _state;   // 0 to prepare, 1 preparing, 2 ready
    internal byte[]? Prepared;
    internal readonly float Gain;   // makes up for a mix turned down to fit 16 bits
    internal nint Native;
    internal volatile bool Failed;

    internal Sound(byte[] file, bool looped, float gain = 1)
    {
        _file = file;
        Looped = looped;
        Gain = gain;
        Seconds = Wav.Read(file, out var pcm) is { } w ? (float)w.Frames / w.Rate : 60;
    }

    /// <summary>True if it plays over and over until stopped.</summary>
    public bool Looped { get; }

    /// <summary>How long it is, in seconds (60 if the file isn't a 16-bit WAV, whose length isn't known).</summary>
    public float Seconds { get; }

    /// <summary>Plays it where you hear from (the camera), at <paramref name="volume"/> (0..1).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public SoundChannel Play(float volume = 1) =>
        Audio.Start(new SoundChannel(this, 0, null, volume, Audio.Caller(Assembly.GetCallingAssembly())));

    /// <summary>Plays it from <paramref name="car"/>, following it: heard from wherever the camera is, like the car's engine.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public SoundChannel PlayOn(Vehicle car, float volume = 1) =>
        Audio.Start(new SoundChannel(this, car.Handle, null, volume, Audio.Caller(Assembly.GetCallingAssembly())));

    /// <summary>Plays it from a spot in the world.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public SoundChannel PlayAt(Vector3 position, float volume = 1) =>
        Audio.Start(new SoundChannel(this, 0, position, volume, Audio.Caller(Assembly.GetCallingAssembly())));

    // On the audio thread: true once it's ready to make. Made mono at the mix rate on a worker thread.
    internal bool Ready(int mixRate)
    {
        if (Volatile.Read(ref _state) == 2) return true;
        if (Interlocked.CompareExchange(ref _state, 1, 0) == 0)
        {
            Task.Run(() =>
            {
                try
                {
                    Prepared = Wav.Read(_file, out var pcm) is { } w ? Wav.Mono16(pcm, w, mixRate > 0 ? mixRate : w.Rate) : _file;
                }
                catch
                {
                    Prepared = _file;
                }
                Volatile.Write(ref _state, 2);
            });
        }
        return false;
    }
}

/// <summary>A sound playing (from <see cref="Sound.Play"/>, <see cref="Sound.PlayOn"/> or <see cref="Sound.PlayAt"/>). Any thread.</summary>
public sealed class SoundChannel
{
    internal const float FadeSeconds = 0.08f;   // fades out rather than cutting the sound mid-wave (a click)

    internal SoundChannel(Sound sound, nint car, Vector3? at, float volume, Assembly? owner)
    {
        Sound = sound;
        Car = car;
        _at = at;
        Volume = volume;
        Owner = owner;
        Created = Environment.TickCount64;
    }

    internal readonly Sound Sound;
    internal readonly nint Car;
    internal readonly Assembly? Owner;
    internal readonly long Created;
    internal nint Native;
    internal float Gain;
    internal long Started;
    internal volatile bool StopWanted, Done;
    private Vector3? _at;

    internal Vector3? At => _at;

    /// <summary>How loud, 0..1.</summary>
    public float Volume { get; set; }

    /// <summary>Full volume within this many metres of it (10)...</summary>
    public float FullVolumeWithin { get; set; } = 10;

    /// <summary>...fading to silence this far away (250).</summary>
    public float SilentBeyond { get; set; } = 250;

    /// <summary>Where it plays from, for one played at a spot (<see cref="Sound.PlayAt"/>): set it to move it.</summary>
    public Vector3? Position
    {
        get => _at;
        set
        {
            if (Car == 0 && _at is not null && value is not null) _at = value;
        }
    }

    /// <summary>True until it has finished or been stopped (and faded out).</summary>
    public bool IsPlaying => !Done;

    /// <summary>Stops it, with a short fade. A sound on a car stops by itself when the car goes (or the game pauses).</summary>
    public void Stop() => StopWanted = true;
}

// 16-bit WAVs, made mono at the game's mix rate through a windowed-sinc filter (a cheap resampler leaves a gritty
// crackle on sounds this bright).
internal static class Wav
{
    internal readonly record struct Format(int Channels, int Rate, int Frames);

    public static Format? Read(byte[] file, out ReadOnlyMemory<byte> pcm)
    {
        pcm = default;
        if (file.Length < 12 || file[0] != 'R' || file[1] != 'I' || file[2] != 'F' || file[3] != 'F' ||
            file[8] != 'W' || file[9] != 'A' || file[10] != 'V' || file[11] != 'E') return null;
        int channels = 0, rate = 0, bits = 0, format = 0;
        for (int at = 12; at + 8 <= file.Length;)
        {
            var id = System.Text.Encoding.ASCII.GetString(file, at, 4);
            int size = BitConverter.ToInt32(file, at + 4);
            int body = at + 8;
            if (size < 0 || body + size > file.Length) size = file.Length - body;
            if (id == "fmt " && size >= 16)
            {
                format = BitConverter.ToUInt16(file, body);
                channels = BitConverter.ToUInt16(file, body + 2);
                rate = BitConverter.ToInt32(file, body + 4);
                bits = BitConverter.ToUInt16(file, body + 14);
                if (format == 0xFFFE && size >= 26) format = BitConverter.ToUInt16(file, body + 24);   // WAVE_FORMAT_EXTENSIBLE: the real tag starts its GUID
            }
            else if (id == "data")
            {
                if (format != 1 || bits != 16 || channels < 1 || rate <= 0) return null;
                pcm = new ReadOnlyMemory<byte>(file, body, size);
                return new Format(channels, rate, size / (2 * channels));
            }
            at = body + size + (size & 1);
        }
        return null;
    }

    // A 16-bit WAV as stereo samples (-1..1, interleaved), mono ones doubled; null for anything else.
    public static (float[] Samples, int Rate)? Stereo(byte[] file)
    {
        if (Read(file, out var pcm) is not { } f || f.Channels > 2) return null;
        var src = MemoryMarshal.Cast<byte, short>(pcm.Span);
        var o = new float[f.Frames * 2];
        for (int i = 0; i < f.Frames; i++)
            for (int c = 0; c < 2; c++)
                o[i * 2 + c] = src[i * f.Channels + (f.Channels == 2 ? c : 0)] / 32768f;
        return (o, f.Rate);
    }

    // Samples (-1..1, interleaved) as a 16-bit WAV; turned down to fit if they go past what 16 bits hold (clipped
    // samples crackle), with gain = how much to turn it back up.
    public static byte[] Write(float[] samples, int channels, int rate, out float gain)
    {
        float peak = 0;
        foreach (var v in samples) peak = Math.Max(peak, MathF.Abs(v));
        float scale = peak > 1 ? 1 / peak : 1;
        gain = 1 / scale;
        var wav = new byte[44 + samples.Length * 2];
        var w = new BinaryWriter(new MemoryStream(wav));
        w.Write("RIFF"u8); w.Write(36 + samples.Length * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate); w.Write(rate * 2 * channels);
        w.Write((short)(2 * channels)); w.Write((short)16);
        w.Write("data"u8); w.Write(samples.Length * 2);
        foreach (var v in samples) w.Write((short)Math.Clamp(MathF.Round(v * scale * 32767f), -32768, 32767));
        return wav;
    }

    public static byte[] Mono16(ReadOnlyMemory<byte> pcm, Format f, int rate)
    {
        var src = MemoryMarshal.Cast<byte, short>(pcm.Span);
        var mono = new float[f.Frames];
        for (int i = 0; i < f.Frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < f.Channels; c++) sum += src[i * f.Channels + c];
            mono[i] = sum / (f.Channels * 32768f);
        }
        var output = rate == f.Rate ? mono : Resample(mono, f.Rate, rate);
        var wav = new byte[44 + output.Length * 2];
        var w = new BinaryWriter(new MemoryStream(wav));
        w.Write("RIFF"u8); w.Write(36 + output.Length * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(output.Length * 2);
        foreach (var s in output) w.Write((short)Math.Clamp(MathF.Round(s * 32767f), -32768, 32767));
        return wav;
    }

    private static float[] Resample(float[] x, int from, int to)
    {
        const int Half = 16;   // taps each side
        double ratio = (double)to / from;
        double cutoff = Math.Min(1.0, ratio);
        double width = Half / cutoff;
        var y = new float[(int)((long)x.Length * to / from)];
        for (int i = 0; i < y.Length; i++)
        {
            double t = i / ratio;
            int lo = (int)Math.Ceiling(t - width), hi = (int)Math.Floor(t + width);
            double sum = 0;
            for (int k = Math.Max(0, lo); k <= Math.Min(x.Length - 1, hi); k++)
            {
                double d = t - k;
                double s = d == 0 ? 1 : Math.Sin(Math.PI * cutoff * d) / (Math.PI * cutoff * d);
                double win = 0.42 + 0.5 * Math.Cos(Math.PI * d / width) + 0.08 * Math.Cos(2 * Math.PI * d / width);   // Blackman
                sum += x[k] * cutoff * s * win;
            }
            y[i] = (float)sum;
        }
        return y;
    }
}
