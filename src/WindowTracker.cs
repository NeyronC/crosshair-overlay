using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using static CrosshairOverlay.Native;

namespace CrosshairOverlay;

public enum TrackState { Disabled, HotkeyOff, NoTarget, TargetNotForeground, Minimized, CursorVisible, Aiming, Shown }

public sealed record WindowInfo(IntPtr Handle, string Title, string ProcessName, int Width, int Height)
{
    public string Display => $"{Title}";
    public string Details => $"{ProcessName}  ·  {Width}×{Height}";
}

public sealed class WindowTracker
{
    readonly AppSettings _s;
    readonly OverlayWindow _overlay;
    readonly DispatcherTimer _timer;
    readonly HashSet<IntPtr> _ownWindows = new();

    IntPtr _lastFg;
    bool _lastFgMatches;
    int _topTick;
    IntPtr _lastShownFg;

    public bool HotkeyOff { get; set; }
    public TrackState State { get; private set; } = TrackState.NoTarget;
    public string TargetInfo { get; private set; } = "";
    public event Action? StateChanged;
        public event Action? GameTick;
    bool _gameFg;

    public WindowTracker(AppSettings s, OverlayWindow overlay)
    {
        _s = s;
        _overlay = overlay;
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Tick();
        s.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.TargetProcess) or nameof(AppSettings.TargetTitle)) _lastFg = IntPtr.Zero;
        };
    }

    public void Start() => _timer.Start();
    public void RegisterOwnWindow(IntPtr h) => _ownWindows.Add(h);
    public void Invalidate() { _lastFg = IntPtr.Zero; Tick(); }

    void Tick()
    {
        _gameFg = false;
        var st = Evaluate(out int cx, out int cy, out string info);
        if (_gameFg)
        {
            GameTick?.Invoke();
            if (HotkeyOff && st == TrackState.Shown) st = TrackState.HotkeyOff;
        }
        if (st == TrackState.Shown)
        {
            _overlay.ShowAt(cx, cy);

            if (_lastShownFg != _lastFg || ++_topTick >= 30) { _topTick = 0; _lastShownFg = _lastFg; _overlay.BringToTop(); }
        }
        else { _overlay.Hide(); _lastShownFg = IntPtr.Zero; }

        var want = _gameFg ? TimeSpan.FromMilliseconds(16) : TimeSpan.FromMilliseconds(100);
        if (_timer.Interval != want) _timer.Interval = want;

        if (st != State || info != TargetInfo)
        {
            State = st;
            TargetInfo = info;
            StateChanged?.Invoke();
        }
    }

    TrackState Evaluate(out int cx, out int cy, out string info)
    {
        cx = cy = 0;
        info = "";
        if (!_s.Enabled) return TrackState.Disabled;
        if (string.IsNullOrWhiteSpace(_s.TargetProcess) && string.IsNullOrWhiteSpace(_s.TargetTitle)) return TrackState.NoTarget;

        IntPtr fg = GetForegroundWindow();
        if (fg != _lastFg)
        {
            _lastFg = fg;
            _lastFgMatches = fg != IntPtr.Zero && !_ownWindows.Contains(fg) && Matches(fg);
        }
        if (!_lastFgMatches) return TrackState.TargetNotForeground;
        if (!IsWindow(fg)) { _lastFg = IntPtr.Zero; return TrackState.TargetNotForeground; }
        if (IsIconic(fg)) return TrackState.Minimized;
        _gameFg = true;

        if (!GetClientRect(fg, out var rc)) return TrackState.TargetNotForeground;
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w < 64 || h < 64) return TrackState.Minimized;
        var origin = new POINT(0, 0);
        ClientToScreen(fg, ref origin);
        info = $"{w}×{h}";

        if (HotkeyOff) return TrackState.HotkeyOff;
        if (_s.HideWhenCursorVisible && IsCursorVisible()) return TrackState.CursorVisible;
        if (_s.HideWhileAiming && IsAimButtonDown()) return TrackState.Aiming;

        var p = _s.Presets.Count > 0 ? _s.Presets[Math.Clamp(_s.ActivePreset, 0, _s.Presets.Count - 1)] : null;
        cx = origin.X + w / 2 + (p?.OffsetX ?? 0);
        cy = origin.Y + h / 2 + (p?.OffsetY ?? 0);
        return TrackState.Shown;
    }

        public static bool IsFireButtonDown()
    {
        int vk = GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_RBUTTON : VK_LBUTTON;
        return (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    static bool IsAimButtonDown()
    {
        int vk = GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_LBUTTON : VK_RBUTTON;
        return (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    bool Matches(IntPtr hwnd)
    {
        var names = ParseNames(_s.TargetProcess);
        if (names.Count == 0 && string.IsNullOrWhiteSpace(_s.TargetTitle)) return false;
        if (names.Count > 0)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            var exe = ProcessNames.Get(pid);
            if (!names.Contains(exe.ToLowerInvariant())) return false;
        }
        if (!string.IsNullOrWhiteSpace(_s.TargetTitle)
            && GetWindowTitle(hwnd).IndexOf(_s.TargetTitle.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        return true;
    }

    static HashSet<string> ParseNames(string s) => s
        .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(n => n.ToLowerInvariant())
        .Select(n => n.EndsWith(".exe") ? n : n + ".exe")
        .ToHashSet();

        public static List<WindowInfo> ListWindows(IReadOnlyCollection<IntPtr> exclude)
    {
        var map = ProcessNames.Snapshot();
        var list = new List<WindowInfo>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || exclude.Contains(h) || IsCloaked(h)) return true;
            if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            if (GetWindow(h, 4 ) != IntPtr.Zero) return true;
            var title = GetWindowTitle(h);
            if (string.IsNullOrWhiteSpace(title)) return true;
            GetClientRect(h, out var rc);
            int w = rc.Right - rc.Left, hh = rc.Bottom - rc.Top;
            if (w < 64 || hh < 64) return true;
            GetWindowThreadProcessId(h, out uint pid);
            map.TryGetValue(pid, out var exe);
            if (title is "Program Manager") return true;
            list.Add(new WindowInfo(h, title, exe ?? "?", w, hh));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}

public static class ProcessNames
{
    public static Dictionary<uint, string> Snapshot()
    {
        var map = new Dictionary<uint, string>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32FirstW(snap, ref e))
                do map[e.th32ProcessID] = e.szExeFile; while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return map;
    }

    public static string Get(uint pid) => Snapshot().TryGetValue(pid, out var n) ? n : "";
}
