using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CrosshairOverlay;

public static class NvidiaProfile
{
    public const uint OGL_CPL_PREFER_DXPRESENT_ID = 0x20D690F8;
    public const uint PreferDisabled = 0, PreferEnabled = 1, PreferAuto = 2;

    public const int NVAPI_OK = 0;
    public const int NVAPI_SETTING_NOT_FOUND = -160;
    public const int NVAPI_EXECUTABLE_NOT_FOUND = -166;
    public const int NVAPI_PROFILE_NOT_FOUND = -163;
    public const int NVAPI_INVALID_USER_PRIVILEGE = -137;

    const int UnicodeLen = 2048;
    const int UnicodeBytes = UnicodeLen * 2;
    const int SettingSize = 12320;
    const int ProfileSize = 4116;
    const int AppSize = 12296;
    static uint Ver(int size, int v) => (uint)size | ((uint)v << 16);

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_Void();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_Session(out IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_SessionOnly(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_FindApp(IntPtr session, IntPtr appName, out IntPtr profile, IntPtr app);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_FindProfile(IntPtr session, IntPtr name, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_CreateProfile(IntPtr session, IntPtr profileInfo, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_CreateApp(IntPtr session, IntPtr profile, IntPtr app);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_SetSetting(IntPtr session, IntPtr profile, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_GetSetting(IntPtr session, IntPtr profile, uint id, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_DeleteSetting(IntPtr session, IntPtr profile, uint id);

    static T? Fn<T>(uint id) where T : Delegate
    {
        IntPtr p = QueryInterface(id);
        return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    public enum Availability { Ok, NoNvidia, Error }

    public sealed record Status(Availability Availability, bool AppFound, uint? Value, bool UserSet, string Message);

        sealed class Session : IDisposable
    {
        public IntPtr Handle;
        readonly D_SessionOnly _destroy;
        public Session(IntPtr h, D_SessionOnly destroy) { Handle = h; _destroy = destroy; }
        public void Dispose() { if (Handle != IntPtr.Zero) _destroy(Handle); Handle = IntPtr.Zero; }
    }

    static bool TryOpen(out Session? s, out string error)
    {
        s = null; error = "";
        try
        {
            var init = Fn<D_Void>(0x0150E828);
            if (init == null || init() != NVAPI_OK) { error = "NVAPI не инициализировался"; return false; }
            var create = Fn<D_Session>(0x0694D52E);
            var destroy = Fn<D_SessionOnly>(0xDAD9CFF8);
            var load = Fn<D_SessionOnly>(0x375DBD6B);
            if (create == null || destroy == null || load == null) { error = "в драйвере нет функций DRS"; return false; }
            int r = create(out IntPtr h);
            if (r != NVAPI_OK) { error = $"CreateSession: {r}"; return false; }
            s = new Session(h, destroy);
            r = load(h);
            if (r != NVAPI_OK) { s.Dispose(); s = null; error = $"LoadSettings: {r}"; return false; }
            return true;
        }
        catch (DllNotFoundException) { error = "nonvidia"; return false; }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    static IntPtr AllocUnicode(string s)
    {
        IntPtr p = Marshal.AllocHGlobal(UnicodeBytes);
        var bytes = new byte[UnicodeBytes];
        var src = Encoding.Unicode.GetBytes(s.Length > UnicodeLen - 1 ? s[..(UnicodeLen - 1)] : s);
        Buffer.BlockCopy(src, 0, bytes, 0, src.Length);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        return p;
    }

    static IntPtr AllocZero(int size, uint version)
    {
        IntPtr p = Marshal.AllocHGlobal(size);
        Marshal.Copy(new byte[size], 0, p, size);
        Marshal.WriteInt32(p, 0, (int)version);
        return p;
    }

    static int FindApp(Session s, string exe, out IntPtr profile)
    {
        var find = Fn<D_FindApp>(0xEEE566B2)!;
        IntPtr name = AllocUnicode(exe), app = AllocZero(AppSize, Ver(AppSize, 1));
        try { return find(s.Handle, name, out profile, app); }
        finally { Marshal.FreeHGlobal(name); Marshal.FreeHGlobal(app); }
    }

        static int GetValue(Session s, IntPtr profile, out uint value, out bool userSet)
    {
        value = 0; userSet = false;
        var get = Fn<D_GetSetting>(0x73BF8338)!;
        IntPtr st = AllocZero(SettingSize, Ver(SettingSize, 1));
        try
        {
            int r = get(s.Handle, profile, OGL_CPL_PREFER_DXPRESENT_ID, st);
            if (r != NVAPI_OK) return r;
            int location = Marshal.ReadInt32(st, 4108);
            int isCurrentPredefined = Marshal.ReadInt32(st, 4112);
            value = (uint)Marshal.ReadInt32(st, 8220);
            userSet = location == 0 && isCurrentPredefined == 0;
            return r;
        }
        finally { Marshal.FreeHGlobal(st); }
    }

    public static Status GetStatus(string exe)
    {
        if (!TryOpen(out var s, out var err))
            return err == "nonvidia"
                ? new Status(Availability.NoNvidia, false, null, false, "Видеокарта не NVIDIA (или драйвер без NVAPI)")
                : new Status(Availability.Error, false, null, false, "NVAPI: " + err);
        using (s)
        {
            int r = FindApp(s!, exe, out IntPtr profile);
            if (r == NVAPI_EXECUTABLE_NOT_FOUND)
                return new Status(Availability.Ok, false, null, false, "Профиль для игры не создан");
            if (r != NVAPI_OK) return new Status(Availability.Error, false, null, false, $"FindApplication: {r}");
            r = GetValue(s!, profile, out uint v, out bool user);
            if (r == NVAPI_SETTING_NOT_FOUND) return new Status(Availability.Ok, true, null, false, "Параметр не задан (по умолчанию)");
            if (r != NVAPI_OK) return new Status(Availability.Error, true, null, false, $"GetSetting: {r}");
            return new Status(Availability.Ok, true, v, user, "");
        }
    }

        public static int Apply(string exe, out string message)
    {
        message = "";
        if (!TryOpen(out var s, out var err)) { message = err; return -1; }
        using (s)
        {
            int r = FindApp(s!, exe, out IntPtr profile);
            if (r == NVAPI_EXECUTABLE_NOT_FOUND)
            {
                r = CreateProfileWithApp(s!, exe, out profile);
                if (r != NVAPI_OK) { message = $"CreateProfile/Application: {r}"; return r; }
            }
            else if (r != NVAPI_OK) { message = $"FindApplication: {r}"; return r; }

            var set = Fn<D_SetSetting>(0x577DD202)!;
            IntPtr st = AllocZero(SettingSize, Ver(SettingSize, 1));
            try
            {
                Marshal.WriteInt32(st, 4100, unchecked((int)OGL_CPL_PREFER_DXPRESENT_ID));
                Marshal.WriteInt32(st, 4104, 0);
                Marshal.WriteInt32(st, 8220, (int)PreferEnabled);
                r = set(s!.Handle, profile, st);
            }
            finally { Marshal.FreeHGlobal(st); }
            if (r != NVAPI_OK) { message = $"SetSetting: {r}"; return r; }
            return Save(s!, out message);
        }
    }

        public static int Revert(string exe, out string message)
    {
        message = "";
        if (!TryOpen(out var s, out var err)) { message = err; return -1; }
        using (s)
        {
            int r = FindApp(s!, exe, out IntPtr profile);
            if (r == NVAPI_EXECUTABLE_NOT_FOUND) return NVAPI_OK;
            if (r != NVAPI_OK) { message = $"FindApplication: {r}"; return r; }
            var del = Fn<D_DeleteSetting>(0xE4A26362)!;
            r = del(s!.Handle, profile, OGL_CPL_PREFER_DXPRESENT_ID);
            if (r == NVAPI_SETTING_NOT_FOUND) return NVAPI_OK;
            if (r != NVAPI_OK) { message = $"DeleteSetting: {r}"; return r; }
            return Save(s!, out message);
        }
    }

    static int CreateProfileWithApp(Session s, string exe, out IntPtr profile)
    {
        profile = IntPtr.Zero;
        string name = "Crosshair Overlay: " + exe;
        var findProfile = Fn<D_FindProfile>(0x7E4A9A0B)!;
        IntPtr pn = AllocUnicode(name);
        int r;
        try { r = findProfile(s.Handle, pn, out profile); }
        finally { Marshal.FreeHGlobal(pn); }
        if (r == NVAPI_PROFILE_NOT_FOUND)
        {
            var createProfile = Fn<D_CreateProfile>(0xCC176068)!;
            IntPtr info = AllocZero(ProfileSize, Ver(ProfileSize, 1));
            try
            {
                var nb = Encoding.Unicode.GetBytes(name);
                Marshal.Copy(nb, 0, info + 4, nb.Length);
                r = createProfile(s.Handle, info, out profile);
            }
            finally { Marshal.FreeHGlobal(info); }
        }
        if (r != NVAPI_OK) return r;

        var createApp = Fn<D_CreateApp>(0x4347A9DE)!;
        IntPtr app = AllocZero(AppSize, Ver(AppSize, 1));
        try
        {
            var ab = Encoding.Unicode.GetBytes(exe);
            Marshal.Copy(ab, 0, app + 8, ab.Length);
            return createApp(s.Handle, profile, app);
        }
        finally { Marshal.FreeHGlobal(app); }
    }

    static int Save(Session s, out string message)
    {
        message = "";
        var save = Fn<D_SessionOnly>(0xFCBC7E14)!;
        int r = save(s.Handle);
        if (r != NVAPI_OK) message = $"SaveSettings: {r}";
        return r;
    }
}
