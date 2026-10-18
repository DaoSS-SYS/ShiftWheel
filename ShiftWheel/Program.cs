using System;
using System.Threading;
using System.Windows;

namespace ShiftWheel;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Command-line mode (also handy for testing): ShiftWheel.exe --convert <file> <fmt:PNG|tool:compress>
        if (args.Length >= 3 && args[0] == "--convert")
        {
            try
            {
                var outs = Converter.RunAsync(args[1], args[2]).GetAwaiter().GetResult();
                foreach (var o in outs) Console.WriteLine(o);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR: " + ex.Message);
                return 1;
            }
        }

        // Dev helper: ShiftWheel.exe --preview out.png sample.png [fmt:JPG] [#background]
        if (args.Length >= 3 && args[0] == "--preview")
        {
            new WheelWindow().RenderPreview(args[1], args[2],
                args.Length > 3 && args[3] != "-" ? args[3] : null,
                args.Length > 4 ? args[4] : "#2B2D31");
            return 0;
        }

        using var mutex =new Mutex(true, "ShiftWheel.SingleInstance", out bool first);
        if (!first) return 0;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // A background tray app should survive a failure in one gesture.
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Write("UNHANDLED: " + e.Exception);
            e.Handled = true;
        };
        using var host = new TrayHost();
        host.Start();
        return app.Run();
    }
}
