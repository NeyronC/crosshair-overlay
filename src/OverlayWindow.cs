using System;
using System.Runtime.InteropServices;
using static CrosshairOverlay.Native;

namespace CrosshairOverlay;

public sealed class OverlayWindow : IDisposable
{
    const string ClassName = "CrosshairOverlay.Layer";
    readonly WndProc _proc;
    public IntPtr Handle { get; }

    CrosshairBitmap? _bmp;
    bool _visible;
    int _x = int.MinValue, _y = int.MinValue;

    public event Action<int>? HotkeyPressed;
    public event Func<int, IntPtr, IntPtr, bool>? MessageReceived;

    public OverlayWindow()
    {
        _proc = WindowProc;
        var hInst = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = hInst,
            lpszClassName = ClassName,
        };
        RegisterClassEx(ref wc);
        Handle = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
            ClassName, "Crosshair Overlay", WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        if (Handle == IntPtr.Zero) throw new InvalidOperationException("Не удалось создать окно оверлея: " + Marshal.GetLastWin32Error());
    }

    IntPtr WindowProc(IntPtr hWnd, int msg, IntPtr w, IntPtr l)
    {
        switch (msg)
        {
            case WM_HOTKEY:
                HotkeyPressed?.Invoke(w.ToInt32());
                return IntPtr.Zero;
            case WM_MOUSEACTIVATE:
                return new IntPtr(MA_NOACTIVATE);
            case WM_NCHITTEST:
                return new IntPtr(HTTRANSPARENT);
        }
        if (MessageReceived != null && MessageReceived(msg, w, l)) return IntPtr.Zero;
        return DefWindowProc(hWnd, msg, w, l);
    }

    public void SetImage(CrosshairBitmap bmp)
    {
        _bmp = bmp;
        _x = int.MinValue;
        if (_visible) Present(_lastCx, _lastCy);
    }

    int _lastCx, _lastCy;

        public void ShowAt(int cx, int cy)
    {
        if (_bmp == null) return;
        _lastCx = cx; _lastCy = cy;
        int x = cx - _bmp.CenterX, y = cy - _bmp.CenterY;
        if (x != _x || y != _y) Present(cx, cy);
        if (!_visible)
        {
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            _visible = true;
        }
    }

        public void BringToTop()
    {
        if (_visible) SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    public void Hide()
    {
        if (!_visible) return;
        ShowWindow(Handle, SW_HIDE);
        _visible = false;
    }

    public bool IsShown => _visible;

    void Present(int cx, int cy)
    {
        var bmp = _bmp!;
        int x = cx - bmp.CenterX, y = cy - bmp.CenterY;
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        var bih = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = bmp.Width, biHeight = -bmp.Height,
            biPlanes = 1, biBitCount = 32,
        };
        IntPtr dib = CreateDIBSection(mem, ref bih, 0, out IntPtr bits, IntPtr.Zero, 0);
        try
        {
            Marshal.Copy(bmp.Pixels, 0, bits, bmp.Pixels.Length);
            IntPtr old = SelectObject(mem, dib);
            var dst = new POINT(x, y);
            var size = new SIZE(bmp.Width, bmp.Height);
            var src = new POINT(0, 0);
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
            UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, ULW_ALPHA);
            SelectObject(mem, old);
            _x = x; _y = y;
        }
        finally
        {
            DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) DestroyWindow(Handle);
    }
}
