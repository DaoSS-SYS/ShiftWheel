using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.IO.Path;
using Shape = System.Windows.Shapes.Shape;
using System.Windows.Threading;

namespace ShiftWheel;

/// <summary>
/// The radial menu. It is a topmost, non-activating, transparent window that is itself an OLE drop target:
/// when the user presses Shift mid-drag it appears under the cursor, learns what is being dragged from
/// DragEnter, builds its sectors for that file type, and converts on Drop.
/// </summary>
internal sealed class WheelWindow : Window
{
    const double Size_ = 400, C = Size_ / 2;
    const double HubR = 42, ToolR0 = 50, ToolR1 = 104, FmtR0 = 112, FmtR1 = 186, BackR = 194;
    const double Gap = 1.6; // degrees between sectors

    // Flat, quiet palette: dark sectors, the hovered one turns white.
    static readonly Brush Back = Frozen("#01000000");        // near-invisible, but keeps the disc hit-testable
    static readonly Brush FmtNormal = Frozen("#EB1E1E22");
    static readonly Brush ToolNormal = Frozen("#EB17171A");
    static readonly Brush Hot = Frozen("#FFFF8A1F");
    static readonly Brush HubFill = Frozen("#F20F0F11");
    static readonly Brush TextNormal = Frozen("#E6FFFFFF");
    static readonly Brush TextDim = Frozen("#99FFFFFF");
    static readonly Brush TextHot = Frozen("#FFFFFFFF");
    static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");

    sealed class Sector
    {
        public required System.Windows.Shapes.Path Shape;
        public required TextBlock Text;
        public required string Action, Label;
        public required double A0, A1, R0, R1;
        public required Brush Normal, TextBrush;
    }

    readonly Canvas _canvas = new() { Width = Size_, Height = Size_ };
    readonly ScaleTransform _scale = new(1, 1);
    TextBlock _hubBig = new(), _hubSmall = new();
    readonly List<Sector> _sectors = new();
    readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    Sector? _hover;
    string[] _files = Array.Empty<string>();
    IntPtr _hwnd;

    public bool IsShown { get; private set; }
    public event Action<string[], string>? ActionChosen;

    public WheelWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        AllowDrop = true;
        Width = Height = Size_;
        Left = Top = -20000;

        var box = new Viewbox { Stretch = Stretch.Uniform, Child = _canvas };
        _canvas.RenderTransformOrigin = new Point(0.5, 0.5);
        _canvas.RenderTransform = _scale;
        Content = box;

        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += (_, _) => SetHover(null);
        Drop += OnDrop;
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); HideNow(); };
    }

    static Brush Frozen(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.MakeNoActivate(_hwnd);
    }

    /// <summary>Creates the native window once (off-screen) so later shows are instant.</summary>
    public void Warmup()
    {
        Build(FileKind.Unknown, Array.Empty<string>());
        Show();
        Native.ShowWindow(_hwnd, Native.SW_HIDE);
    }

    // ───────────────────────────── show / hide ─────────────────────────────

    public void ShowAt(int x, int y)
    {
        _hideTimer.Stop();
        _files = Array.Empty<string>();
        Build(FileKind.Unknown, _files);

        Log.Write($"ShowAt {x},{y}");
        var mon = Native.MonitorFromPoint(new Native.POINT { X = x, Y = y }, 2);
        uint dpi = 96;
        try { Native.GetDpiForMonitor(mon, 0, out dpi, out _); } catch { }
        int size = (int)Math.Round(Size_ * dpi / 96.0);

        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).WorkingArea;
        int left = Math.Clamp(x - size / 2, area.Left, Math.Max(area.Left, area.Right - size));
        int top = Math.Clamp(y - size / 2, area.Top, Math.Max(area.Top, area.Bottom - size));

        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, left, top, size, size,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        IsShown = true;

        _scale.ScaleX = _scale.ScaleY = 0.82;
        var anim = new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)) { EasingFunction = new BackEase { Amplitude = 0.4 } };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);

        // The drag loop only talks to a new drop target after the next mouse move: nudge by a pixel so the
        // wheel gets DragEnter (and learns the file type) immediately instead of after the user moves.
        var nudge = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        nudge.Tick += (_, _) =>
        {
            nudge.Stop();
            if (!IsShown) return;
            Native.mouse_event(0x0001, 1, 0, 0, UIntPtr.Zero);
            Native.mouse_event(0x0001, -1, 0, 0, UIntPtr.Zero);
        };
        nudge.Start();
    }

    /// <summary>Called when the mouse button is released: gives Drop a moment to arrive, then hides.</summary>
    public void ScheduleHide()
    {
        if (!IsShown) return;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    public void HideNow()
    {
        _hideTimer.Stop();
        if (!IsShown) return;
        IsShown = false;
        Native.ShowWindow(_hwnd, Native.SW_HIDE);
        _hover = null;
    }

    // ───────────────────────────── drag & drop ─────────────────────────────

    string[] FilesFrom(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] f)
            return f.Where(File.Exists).ToArray();
        return Array.Empty<string>();
    }

    void OnDragEnter(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        var files = FilesFrom(e.Data);
        if (files.Length == 0 || files.SequenceEqual(_files)) return;

        _files = files;
        var kinds = files.Select(Formats.Classify).Distinct().ToList();
        var kind = kinds.Count == 1 ? kinds[0] : FileKind.Unknown;
        Build(kind, files, mixed: kinds.Count > 1);
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        if (_files.Length == 0) OnDragEnter(sender, e);
        SetHover(HitTest(e.GetPosition(_canvas)));
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        var sector = HitTest(e.GetPosition(_canvas));
        var files = _files.Length > 0 ? _files : FilesFrom(e.Data);
        HideNow();
        if (sector == null || files.Length == 0) return;

        string action = sector.Action;
        // Don't run the conversion inside the drop call: Explorer is blocked until it returns.
        Dispatcher.BeginInvoke(() => ActionChosen?.Invoke(files, action), DispatcherPriority.Background);
    }

    Sector? HitTest(Point p)
    {
        double dx = p.X - C, dy = p.Y - C;
        double r = Math.Sqrt(dx * dx + dy * dy);
        double ang = Math.Atan2(dx, -dy) * 180 / Math.PI; // 0° at top, clockwise
        if (ang < 0) ang += 360;
        return _sectors.FirstOrDefault(s =>
            r >= s.R0 && r < s.R1 && InArc(ang, s.A0, s.A1));
    }

    static bool InArc(double ang, double a0, double a1)
    {
        a0 = ((a0 % 360) + 360) % 360;
        a1 = ((a1 % 360) + 360) % 360;
        return a0 <= a1 ? ang >= a0 && ang < a1 : ang >= a0 || ang < a1;
    }

    void SetHover(Sector? s)
    {
        if (ReferenceEquals(s, _hover)) return;
        if (_hover != null)
        {
            _hover.Shape.Fill = _hover.Normal;
            _hover.Text.Foreground = _hover.TextBrush;
        }
        _hover = s;
        if (s != null)
        {
            s.Shape.Fill = Hot;
            s.Text.Foreground = TextHot;
        }
        UpdateHub();
    }

    // ───────────────────────────── drawing ─────────────────────────────

    string HubFileText() => _files.Length switch
    {
        0 => "…",
        1 => Shorten(Path.GetFileName(_files[0]), 20),
        var n => $"{n} файл(ов)",
    };

    static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    void UpdateHub()
    {
        if (_hover != null)
        {
            bool tool = _hover.Action.StartsWith("tool:");
            _hubBig.Text = _hover.Action switch
            {
                "tool:compress" => "Сжать",
                "tool:strip" => "Убрать метаданные",
                "tool:resize" => "Размер 50%",
                "tool:rotate" => "Повернуть на 90°",
                _ => _hover.Label,
            };
            _hubBig.FontSize = tool ? 11 : 17;
            _hubSmall.Text = HubFileText();
        }
        else
        {
            _hubBig.FontSize = 12;
            _hubBig.Text = _files.Length == 0 ? "" : "Куда?";
            _hubSmall.Text = _files.Length == 0 ? "" : HubFileText();
        }
    }

    void Build(FileKind kind, string[] files, bool mixed = false)
    {
        _canvas.Children.Clear();
        _sectors.Clear();
        _hover = null;

        var back = new Ellipse
        {
            Width = BackR * 2, Height = BackR * 2, Fill = Back, AllowDrop = true,
        };
        Canvas.SetLeft(back, C - BackR);
        Canvas.SetTop(back, C - BackR);
        _canvas.Children.Add(back);

        string ext = files.Length > 0 ? Formats.Ext(files[0]) : "";
        var targets = Formats.Targets(kind, ext);
        var tools = Formats.Tools(kind);

        AddRing(targets.Select(t => (t, "fmt:" + t)).ToList(), FmtR0, FmtR1, FmtNormal, TextNormal, 14, FontWeights.Medium);
        AddRing(tools.Select(t => (t.Label, "tool:" + t.Id)).ToList(), ToolR0, ToolR1, ToolNormal, TextDim, 10.5, FontWeights.Normal);

        var hub = new Ellipse
        {
            Width = HubR * 2, Height = HubR * 2, Fill = HubFill, IsHitTestVisible = false,
        };
        Canvas.SetLeft(hub, C - HubR);
        Canvas.SetTop(hub, C - HubR);
        _canvas.Children.Add(hub);

        _files = files;
        _hubBig = new TextBlock
        {
            Foreground = Brushes.White, FontFamily = Font, FontWeight = FontWeights.Medium, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, Width = HubR * 2 - 12, IsHitTestVisible = false,
            Margin = new Thickness(0, 0, 0, 1),
        };
        _hubSmall = new TextBlock
        {
            Foreground = TextDim, FontFamily = Font, FontSize = 9, TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Width = HubR * 2 - 12, IsHitTestVisible = false,
        };

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        stack.Children.Add(_hubBig);
        stack.Children.Add(_hubSmall);
        var host = new Grid { Width = HubR * 2, Height = HubR * 2, IsHitTestVisible = false };
        host.Children.Add(stack);
        Canvas.SetLeft(host, C - HubR);
        Canvas.SetTop(host, C - HubR);
        _canvas.Children.Add(host);

        if (mixed) { _hubBig.Text = "Разные\nтипы файлов"; _hubSmall.Text = "выбери файлы\nодного типа"; }
        else if (kind == FileKind.Unknown && files.Length > 0) { _hubBig.Text = "Не\nподдерживается"; _hubSmall.Text = HubFileText(); }
        else UpdateHub();
    }

    void AddRing(List<(string Label, string Action)> items, double r0, double r1, Brush normal, Brush textBrush, double font, FontWeight weight)
    {
        int n = items.Count;
        if (n == 0) return;
        double span = 360.0 / n;
        for (int i = 0; i < n; i++)
        {
            double center = i * span;               // first sector centred at the top
            double a0 = center - span / 2 + Gap / 2;
            double a1 = center + span / 2 - Gap / 2;

            var shape = new System.Windows.Shapes.Path
            {
                Data = Wedge(a0, a1, r0, r1),
                Fill = normal, IsHitTestVisible = false,
            };
            _canvas.Children.Add(shape);

            var label = new TextBlock
            {
                Text = items[i].Label, Foreground = textBrush, FontSize = font, FontFamily = Font,
                FontWeight = weight, IsHitTestVisible = false,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                MaxWidth = r0 < FmtR0 ? 62 : 80,
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var mid = Pt(center, (r0 + r1) / 2);
            Canvas.SetLeft(label, mid.X - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, mid.Y - label.DesiredSize.Height / 2);
            _canvas.Children.Add(label);

            _sectors.Add(new Sector
            {
                Shape = shape, Text = label, Action = items[i].Action, Label = items[i].Label,
                A0 = a0, A1 = a1, R0 = r0, R1 = r1, Normal = normal, TextBrush = textBrush,
            });
        }
    }

    /// <summary>Dev helper: renders the wheel for a sample file over a flat background into a PNG.</summary>
    public void RenderPreview(string pngPath, string sampleFile, string? hoverAction, string backgroundHex)
    {
        Build(Formats.Classify(sampleFile), new[] { sampleFile });
        if (hoverAction != null) SetHover(_sectors.First(s => s.Action == hoverAction));
        _canvas.Measure(new Size(Size_, Size_));
        _canvas.Arrange(new Rect(0, 0, Size_, Size_));
        _canvas.UpdateLayout();

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Frozen(backgroundHex), null, new Rect(0, 0, Size_, Size_));
            dc.DrawRectangle(new VisualBrush(_canvas), null, new Rect(0, 0, Size_, Size_));
        }
        var rtb = new RenderTargetBitmap((int)Size_ * 2, (int)Size_ * 2, 192, 192, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(pngPath);
        enc.Save(fs);
    }

    static Point Pt(double deg, double r)
    {
        double a = deg * Math.PI / 180;
        return new Point(C + r * Math.Sin(a), C - r * Math.Cos(a));
    }

    static Geometry Wedge(double a0, double a1, double r0, double r1)
    {
        bool large = a1 - a0 > 180;
        var fig = new PathFigure { StartPoint = Pt(a0, r1), IsClosed = true };
        fig.Segments.Add(new ArcSegment(Pt(a1, r1), new Size(r1, r1), 0, large, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(Pt(a1, r0), true));
        fig.Segments.Add(new ArcSegment(Pt(a0, r0), new Size(r0, r0), 0, large, SweepDirection.Counterclockwise, true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        g.Freeze();
        return g;
    }
}
