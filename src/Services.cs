using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Microsoft.Win32;
using static CrosshairOverlay.Native;

namespace CrosshairOverlay;

public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "CrosshairOverlay";

    static string ExePath => Environment.ProcessPath ?? "";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is string v && v.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (on) k.SetValue(ValueName, $"\"{ExePath}\" --tray");
        else if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
    }
}

public sealed class TrayIcon : IDisposable
{
    const int CallbackMsg = WM_APP + 1;
    readonly OverlayWindow _host;
    readonly uint _taskbarCreated;
    IntPtr _icon;
    bool _added;

    public event Action? LeftClick;
    public event Action? RightClick;

    public TrayIcon(OverlayWindow host)
    {
        _host = host;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        var small = new IntPtr[1];
        if (ExtractIconEx(Environment.ProcessPath ?? "", 0, null, small, 1) > 0) _icon = small[0];
        host.MessageReceived += OnMessage;
        Add();
    }

    NOTIFYICONDATA Data(string tip = "Crosshair Overlay") => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _host.Handle,
        uID = 1,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
        uCallbackMessage = CallbackMsg,
        hIcon = _icon,
        szTip = tip, szInfo = "", szInfoTitle = "",
        uVersion = 4,
    };

    void Add()
    {
        var d = Data();
        _added = Shell_NotifyIcon(NIM_ADD, ref d);
        Shell_NotifyIcon(NIM_SETVERSION, ref d);
    }

    public void SetTooltip(string tip)
    {
        if (!_added) return;
        var d = Data(tip.Length > 120 ? tip[..120] : tip);
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    bool OnMessage(int msg, IntPtr w, IntPtr l)
    {
        if (msg == CallbackMsg)
        {
            int ev = (int)(l.ToInt64() & 0xFFFF);
            if (ev == WM_LBUTTONUP) LeftClick?.Invoke();
            else if (ev == WM_CONTEXTMENU) RightClick?.Invoke();
            return true;
        }
        if (msg == (int)_taskbarCreated) { Add(); return true; }
        return false;
    }

    public void Dispose()
    {
        if (_added) { var d = Data(); Shell_NotifyIcon(NIM_DELETE, ref d); _added = false; }
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
    }
}
