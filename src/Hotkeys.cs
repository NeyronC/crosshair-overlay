using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CrosshairOverlay;

public sealed class Chord
{
    const int WinKey = -1;
    public IReadOnlyList<int> Keys { get; }
    bool _wasDown;

    Chord(List<int> keys) { Keys = keys; }

    static readonly Dictionary<string, int> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = 0x11, ["Alt"] = 0x12, ["Shift"] = 0x10, ["Win"] = WinKey,
        ["Mouse3"] = 0x04, ["Mouse4"] = 0x05, ["Mouse5"] = 0x06,
    };

    public static Chord? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var keys = new List<int>();
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int vk;
            if (Named.TryGetValue(raw, out int n)) vk = n;
            else if (raw.Length == 1 && char.IsAsciiLetterOrDigit(raw[0])) vk = char.ToUpperInvariant(raw[0]);
            else if (Enum.TryParse<Key>(raw, true, out var key)) vk = KeyInterop.VirtualKeyFromKey(key);
            else return null;
            if (vk != 0 && !keys.Contains(vk)) keys.Add(vk);
        }
        return keys.Count > 0 ? new Chord(keys) : null;
    }

    static bool Down(int vk) => vk == WinKey
        ? (Native.GetAsyncKeyState(0x5B) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x5C) & 0x8000) != 0
        : (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

        public bool Poll(out bool allDown)
    {
        allDown = Keys.All(Down);
        bool fired = allDown && !_wasDown;
        _wasDown = allDown;
        return fired;
    }

    public bool Contains(Chord other) => other.Keys.All(Keys.Contains);

    public static string? NameOf(Key k) => k switch
    {
        Key.LeftCtrl or Key.RightCtrl => "Ctrl",
        Key.LeftAlt or Key.RightAlt => "Alt",
        Key.LeftShift or Key.RightShift => "Shift",
        Key.LWin or Key.RWin => "Win",
        >= Key.D0 and <= Key.D9 => ((char)('0' + (k - Key.D0))).ToString(),
        >= Key.A and <= Key.Z => k.ToString(),
        Key.None or Key.ImeProcessed or Key.DeadCharProcessed => null,
        _ => k.ToString(),
    };

    public static string? NameOf(MouseButton b) => b switch
    {
        MouseButton.Middle => "Mouse3",
        MouseButton.XButton1 => "Mouse4",
        MouseButton.XButton2 => "Mouse5",
        _ => null,
    };

    static readonly string[] ModOrder = { "Ctrl", "Alt", "Shift", "Win" };

    public static string Format(IEnumerable<string> names)
    {
        var list = names.Distinct().ToList();
        var mods = ModOrder.Where(list.Contains);
        return string.Join("+", mods.Concat(list.Where(n => !ModOrder.Contains(n))));
    }
}

public sealed class HotkeyBox : TextBox
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((HotkeyBox)d).ShowValue()));

    public string Hotkey { get => (string)GetValue(HotkeyProperty); set => SetValue(HotkeyProperty, value); }

    readonly HashSet<string> _held = new();
    readonly List<string> _captured = new();

    public HotkeyBox()
    {
        SetResourceReference(StyleProperty, typeof(TextBox));
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        FontFamily = new FontFamily("Cascadia Mono, Consolas");
        GotKeyboardFocus += (_, _) => { Reset(); Text = "нажмите сочетание…"; };
        LostKeyboardFocus += (_, _) => { Reset(); ShowValue(); };
        ShowValue();
    }

    void Reset() { _held.Clear(); _captured.Clear(); }
    void ShowValue() { if (!IsKeyboardFocused) Text = string.IsNullOrWhiteSpace(Hotkey) ? "не задано" : Hotkey; }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_captured.Count == 0 && key == Key.Escape) { Keyboard.ClearFocus(); return; }
        if (_captured.Count == 0 && key is Key.Back or Key.Delete) { Hotkey = ""; Keyboard.ClearFocus(); return; }
        var name = Chord.NameOf(key);
        if (name == null || e.IsRepeat) return;
        _held.Add(name);
        if (!_captured.Contains(name)) _captured.Add(name);
        Text = Chord.Format(_captured) + " …";
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        e.Handled = true;
        var name = Chord.NameOf(e.Key == Key.System ? e.SystemKey : e.Key);
        if (name != null) _held.Remove(name);
        TryCommit();
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (!IsKeyboardFocused) return;
        var name = Chord.NameOf(e.ChangedButton);
        if (name == null) return;
        e.Handled = true;
        _held.Add(name);
        if (!_captured.Contains(name)) _captured.Add(name);
        Text = Chord.Format(_captured) + " …";
    }

    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        var name = Chord.NameOf(e.ChangedButton);
        if (name == null) return;
        e.Handled = true;
        _held.Remove(name);
        TryCommit();
    }

    void TryCommit()
    {
        if (_held.Count > 0 || _captured.Count == 0) return;

        if (_captured.All(n => n is "Ctrl" or "Alt" or "Shift" or "Win")) { Reset(); Text = "нажмите сочетание…"; return; }
        Hotkey = Chord.Format(_captured);
        Reset();
        Keyboard.ClearFocus();
    }
}
