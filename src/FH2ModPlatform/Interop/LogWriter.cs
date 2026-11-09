using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace FH2ModPlatform.Interop;

/// <summary>
/// A TextWriter whose lines go to platform.log, for Console.Out, Console.Error
/// and Trace/Debug. Mods write from game threads, so a write only queues the
/// line: a background thread does the logging, and a hook's Console.WriteLine
/// never waits on the disk.
/// </summary>
internal sealed class LogWriter : TextWriter
{
    private readonly Action<string> _log;
    private readonly string _prefix;
    private readonly StringBuilder _line = new();
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 10_000);

    public LogWriter(Action<string> log, string prefix)
    {
        _log = log;
        _prefix = prefix;
        new Thread(Drain) { IsBackground = true, Name = "FH2ModPlatform log writer" }.Start();
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_line)
        {
            if (value == '\n') Emit();
            else if (value != '\r') _line.Append(value);
        }
    }

    public override void Write(string? value)
    {
        if (value is null) return;
        lock (_line)
        {
            foreach (var c in value)
            {
                if (c == '\n') Emit();
                else if (c != '\r') _line.Append(c);
            }
        }
    }

    public override void Flush()
    {
        lock (_line)
            if (_line.Length > 0) Emit();
    }

    private void Emit()
    {
        // Full queue (something printing in a tight loop): drop rather than stall the game.
        _queue.TryAdd(_prefix + _line);
        _line.Clear();
    }

    private void Drain()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try { _log(line); } catch { }
        }
    }

    /// <summary>Points Console.Out, Console.Error and Trace/Debug listeners at <paramref name="log"/>.</summary>
    public static void Redirect(Action<string> log)
    {
        var output = new LogWriter(log, "console: ");
        Console.SetOut(output);
        Console.SetError(new LogWriter(log, "console error: "));
        // Debug.WriteLine (Debug builds) and Trace.WriteLine share these listeners.
        Trace.Listeners.Add(new TextWriterTraceListener(new LogWriter(log, "trace: ")));
        Trace.AutoFlush = true;
    }
}
