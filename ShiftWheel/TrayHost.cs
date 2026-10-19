using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ShiftWheel;

/// <summary>Wires everything together: tray icon, input hooks, wheel overlay, conversion jobs and toasts.</summary>
internal sealed class TrayHost : IDisposable
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string AppName = "ShiftWheel";

    readonly NotifyIcon _tray;
    readonly InputWatcher _input = new();
    readonly WheelWindow _wheel = new();
    readonly ToastHost _toasts = new();
    readonly ToolStripMenuItem _autostart;

    public TrayHost()
    {
        _autostart = new ToolStripMenuItem("Запускать вместе с Windows") { CheckOnClick = true, Checked = IsAutostart() };
        _autostart.CheckedChanged += (_, _) => SetAutostart(_autostart.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Зажми файл и нажми Shift") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autostart);
        menu.Items.Add("Выход", null, (_, _) => System.Windows.Application.Current.Shutdown());

        _tray = new NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "ShiftWheel — перетащи файл и нажми Shift",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _wheel.ActionChosen += OnAction;
        _input.ShowRequested += (x, y) => _wheel.ShowAt(x, y);
        _input.ButtonReleased += _wheel.ScheduleHide;
        _input.Cancelled += _wheel.HideNow;
    }

    public void Start()
    {
        _wheel.Warmup();
        _input.Start();

        if (FfmpegLocator.Find() == null)
        {
            _toasts.Begin("ffmpeg не найден", "Для видео, аудио и картинок нужен ffmpeg: winget install Gyan.FFmpeg")
                   .Fail("Для видео, аудио и картинок нужен ffmpeg. Установи: winget install Gyan.FFmpeg");
        }
        else
        {
            var t = _toasts.Begin("ShiftWheel запущен", "Зажми файл, перетащи и нажми Shift");
            t.Done(Array.Empty<string>());
        }
    }

    async void OnAction(string[] files, string action)
    {
        string what = action.StartsWith("fmt:")
            ? "→ " + action[4..]
            : action[5..] switch
            {
                "compress" => "Сжимаю…", "strip" => "Удаляю метаданные…",
                "resize" => "Уменьшаю до 50%…", "rotate" => "Поворачиваю…", _ => "Обрабатываю…",
            };

        foreach (var file in files)
        {
            var toast = _toasts.Begin(Path.GetFileName(file), what);
            try
            {
                var progress = new Progress<double>(toast.SetProgress);
                var outputs = await Task.Run(() => Converter.RunAsync(file, action, progress));
                toast.Done(outputs);
            }
            catch (Exception ex)
            {
                toast.Fail(ex.Message);
            }
        }
    }

    // ───────────── autostart ─────────────

    static bool IsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(AppName) == null) return false;

        // Honour the "disabled" switch a user can flip in Task Manager → Startup apps.
        using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKey);
        return approved?.GetValue(AppName) is not byte[] { Length: > 0 } flag || (flag[0] & 1) == 0;
    }

    static void SetAutostart(bool on)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key == null) return;
        if (on) key.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(AppName, throwOnMissingValue: false);

        // Windows 11 only starts Run entries that Task Manager's "Startup apps" list marks as enabled
        // (02 = enabled, 03 = disabled by the user), so write that flag too.
        using var approved = Registry.CurrentUser.CreateSubKey(StartupApprovedKey, writable: true);
        if (on) approved.SetValue(AppName, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        else approved.DeleteValue(AppName, throwOnMissingValue: false);
    }

    // ───────────── tray icon: an orange wheel ─────────────

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var orange = new SolidBrush(Color.FromArgb(255, 255, 138, 31));
            g.FillEllipse(orange, 1, 1, 29, 29);
            using var pen = new Pen(Color.FromArgb(255, 20, 20, 24), 2f);
            g.DrawEllipse(pen, 9, 9, 13, 13);
            for (int i = 0; i < 4; i++)
            {
                double a = i * Math.PI / 2 + Math.PI / 4;
                g.DrawLine(pen, 16 + (float)(7 * Math.Cos(a)), 16 + (float)(7 * Math.Sin(a)),
                                16 + (float)(14 * Math.Cos(a)), 16 + (float)(14 * Math.Sin(a)));
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _input.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
    }
}
