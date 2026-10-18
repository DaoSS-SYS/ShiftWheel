using System;
using System.IO;

namespace ShiftWheel;

/// <summary>Opt-in diagnostics: set SHIFTWHEEL_DEBUG=1 to write %TEMP%\shiftwheel.log.</summary>
internal static class Log
{
    static readonly bool On = Environment.GetEnvironmentVariable("SHIFTWHEEL_DEBUG") == "1";
    static readonly string PathName = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shiftwheel.log");
    static readonly object Gate = new();

    public static void Write(string msg)
    {
        if (!On) return;
        try { lock (Gate) File.AppendAllText(PathName, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); } catch { }
    }
}
