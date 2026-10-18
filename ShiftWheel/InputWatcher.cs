using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Threading;

namespace ShiftWheel;

/// <summary>
/// Global low-level mouse/keyboard hooks. Raises <see cref="ShowRequested"/> when the user is
/// dragging an Explorer/desktop item with the left button held and presses Shift.
/// </summary>
internal sealed class InputWatcher : IDisposable
{
    readonly Native.HookProc _mouseProc;
    readonly Native.HookProc _keyProc;
    IntPtr _mouseHook, _keyHook;
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;

    bool _leftDown, _dragging, _shown;
    Native.POINT _downPt;
    int _gen;
    volatile int _source; // 0 = unknown yet, 1 = Explorer item, 2 = something else

    public event Action<int, int>? ShowRequested;
    public event Action? ButtonReleased;
    public event Action? Cancelled;

    public InputWatcher()
    {
        _mouseProc = MouseProc;
        _keyProc = KeyProc;
    }

    public void Start()
    {
        var mod = Native.GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName);
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, mod, 0);
        _keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyProc, mod, 0);
        if (_mouseHook == IntPtr.Zero || _keyHook == IntPtr.Zero)
            throw new InvalidOperationException("Не удалось установить глобальные хуки ввода.");
    }

    IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                switch ((int)wParam)
                {
                    case Native.WM_LBUTTONDOWN: OnDown(info.pt); break;
                    case Native.WM_MOUSEMOVE: OnMove(info.pt); break;
                    case Native.WM_LBUTTONUP: OnUp(); break;
                }
            }
            catch { /* never let an exception escape a hook */ }
        }
        return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    IntPtr KeyProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && ((int)wParam == Native.WM_KEYDOWN || (int)wParam == Native.WM_SYSKEYDOWN))
        {
            try
            {
                int vk = (int)Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam).vkCode;
                if (vk is Native.VK_SHIFT or Native.VK_LSHIFT or Native.VK_RSHIFT)
                {
                    Log.Write($"shift down leftDown={_leftDown} dragging={_dragging} source={_source}");
                    if (_leftDown && _dragging) TryShow(shiftKnown: true);
                }
                else if (vk == Native.VK_ESCAPE && _shown)
                {
                    _shown = false;
                    _ui.BeginInvoke(() => Cancelled?.Invoke());
                }
            }
            catch { }
        }
        return Native.CallNextHookEx(_keyHook, code, wParam, lParam);
    }

    void OnDown(Native.POINT pt)
    {
        _leftDown = true;
        _dragging = false;
        _shown = false;
        _downPt = pt;
        _source = 0;
        int gen = ++_gen;
        // Resolve what was clicked off the hook thread: UI Automation calls are too slow for a hook.
        Task.Run(() =>
        {
            int r = Evaluate(pt);
            Log.Write($"down at {pt.X},{pt.Y} -> source={r}");
            if (gen == _gen) _source = r;
        });
    }

    void OnMove(Native.POINT pt)
    {
        if (!_leftDown) return;
        if (!_dragging)
        {
            int dx = pt.X - _downPt.X, dy = pt.Y - _downPt.Y;
            if (dx * dx + dy * dy < 64) return;
            _dragging = true;
            Log.Write("drag started");
        }
        TryShow(shiftKnown: false);
    }

    void OnUp()
    {
        _leftDown = false;
        _dragging = false;
        if (_shown)
        {
            _shown = false;
            _ui.BeginInvoke(() => ButtonReleased?.Invoke());
        }
    }

    void TryShow(bool shiftKnown)
    {
        if (_shown || _source != 1) return;
        if (!shiftKnown && (Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) == 0) return;
        _shown = true;
        Native.GetCursorPos(out var p);
        Log.Write($"show requested at {p.X},{p.Y}");
        _ui.BeginInvoke(() => ShowRequested?.Invoke(p.X, p.Y));
    }

    static int Evaluate(Native.POINT pt)
    {
        try
        {
            var hwnd = Native.WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return 2;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Environment.ProcessId) return 2;

            string name;
            try { name = Process.GetProcessById((int)pid).ProcessName; }
            catch { return 2; }
            Log.Write($"window under cursor belongs to {name}");
            if (!name.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return 2;

            return IsFileItem(pt) ? 1 : 2;
        }
        catch { return 2; }
    }

    /// <summary>True when the point is over a file/folder item (not empty space, not a rubber-band selection).</summary>
    static bool IsFileItem(Native.POINT pt)
    {
        try
        {
            var el = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
            for (int i = 0; el != null && i < 4; i++)
            {
                var t = el.Current.ControlType;
                if (t == ControlType.ListItem || t == ControlType.DataItem) return true;
                el = TreeWalker.ControlViewWalker.GetParent(el);
            }
            return false;
        }
        catch
        {
            return true; // UIA failed: don't block the feature
        }
    }

    public void Dispose()
    {
        if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = IntPtr.Zero;
    }
}
