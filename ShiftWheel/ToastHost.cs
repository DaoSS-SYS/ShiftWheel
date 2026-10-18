using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ShiftWheel;

/// <summary>Bottom-right stack of small status cards ("Converting…", "Done", errors).</summary>
internal sealed class ToastHost : Window
{
    readonly StackPanel _stack = new() { Orientation = Orientation.Vertical };
    int _count;

    public ToastHost()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 372;
        Content = _stack;
        SizeChanged += (_, _) => Reposition();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.MakeNoActivate(new WindowInteropHelper(this).Handle);
    }

    void Reposition()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 8;
        Top = wa.Bottom - ActualHeight - 8;
    }

    public Toast Begin(string title, string subtitle)
    {
        var toast = new Toast(this, title, subtitle);
        _stack.Children.Add(toast.Root);
        _count++;
        if (!IsVisible) Show();
        Reposition();
        return toast;
    }

    internal void Remove(Toast t)
    {
        _stack.Children.Remove(t.Root);
        if (--_count <= 0) { _count = 0; Hide(); }
    }
}

internal sealed class Toast
{
    readonly ToastHost _host;
    readonly TextBlock _title = new(), _sub = new();
    readonly ProgressBar _bar = new() { Height = 2, Minimum = 0, Maximum = 1, IsIndeterminate = true, Margin = new Thickness(0, 7, 0, 0) };
    readonly DispatcherTimer _timer = new();
    string? _reveal;

    public Border Root { get; }

    public Toast(ToastHost host, string title, string subtitle)
    {
        _host = host;
        _title.Text = title;
        _title.Foreground = Brushes.White;
        _title.FontWeight = FontWeights.Medium;
        _title.FontFamily = _sub.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        _title.FontSize = 13;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _sub.Text = subtitle;
        _sub.Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0xB4, 0xC0));
        _sub.FontSize = 12;
        _sub.TextWrapping = TextWrapping.Wrap;
        _bar.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x1F));
        _bar.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x46));
        _bar.BorderThickness = new Thickness(0);

        var panel = new StackPanel();
        panel.Children.Add(_title);
        panel.Children.Add(_sub);
        panel.Children.Add(_bar);

        Root = new Border
        {
            Child = panel,
            Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x17, 0x17, 0x19)),

            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 11),
            Margin = new Thickness(0, 6, 0, 0),
        };
        Root.MouseLeftButtonUp += OnClick;
        _timer.Tick += (_, _) => Close();
    }

    public void SetProgress(double v)
    {
        _bar.IsIndeterminate = false;
        _bar.Value = v;
    }

    public void Done(string[] outputs)
    {
        _bar.Visibility = Visibility.Collapsed;
        _title.Text = "✓ " + (outputs.Length == 1 ? Path.GetFileName(outputs[0]) : $"Готово: {outputs.Length} файл(ов)");
        _sub.Text = "Нажми, чтобы показать в папке";
        _reveal = outputs.Length > 0 ? outputs[0] : null;
        Root.Cursor = Cursors.Hand;
        Schedule(7);
    }

    public void Fail(string message)
    {
        _bar.Visibility = Visibility.Collapsed;
        _title.Text = "✕ Не удалось";
        _title.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A));
        _sub.Text = message;
        Schedule(12);
    }

    void Schedule(double seconds)
    {
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(seconds);
        _timer.Start();
    }

    void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (_reveal != null && File.Exists(_reveal))
        {
            try { Process.Start("explorer.exe", $"/select,\"{_reveal}\""); } catch { }
        }
        Close();
    }

    void Close()
    {
        _timer.Stop();
        _host.Remove(this);
    }
}
