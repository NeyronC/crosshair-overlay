using System.Collections.Generic;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CrosshairOverlay;

public partial class App : Application
{
    const string MutexName = "Local\\CrosshairOverlay.Singleton";
    const string ShowEventName = "Local\\CrosshairOverlay.Show";

    Mutex? _mutex;
    static bool _errorShown;
    EventWaitHandle? _showEvent;

    public AppSettings Settings { get; private set; } = null!;
    public OverlayWindow Overlay { get; private set; } = null!;
    public WindowTracker Tracker { get; private set; } = null!;
    TrayIcon? _tray;
    MainWindow? _main;
    Preset? _subscribed;
    DispatcherTimer _saveTimer = null!;

    public static new App Current => (App)Application.Current;
    public Preset ActivePreset => Settings.Presets[Math.Clamp(Settings.ActivePreset, 0, Settings.Presets.Count - 1)];
    public event Action? ActivePresetChanged;
    public event Action? HotkeyChanged;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length >= 2 && e.Args[0] == "--selftest")
        {
            System.IO.File.WriteAllText(e.Args[1], SelfTest.Run());
            Shutdown();
            return;
        }

        if (e.Args.Length >= 3 && e.Args[0] == "--nvidia")
        {
            string exe = e.Args[2], msg = "";
            int code = 0;
            switch (e.Args[1])
            {
                case "apply": code = NvidiaProfile.Apply(exe, out msg); break;
                case "revert": code = NvidiaProfile.Revert(exe, out msg); break;
                default:
                    var st = NvidiaProfile.GetStatus(exe);
                    msg = $"{st.Availability} appFound={st.AppFound} value={st.Value?.ToString() ?? "null"} userSet={st.UserSet} {st.Message}";
                    break;
            }
            if (e.Args.Length >= 4) System.IO.File.WriteAllText(e.Args[3], $"{code} {msg}");
            Shutdown(code == 0 ? 0 : 1);
            return;
        }

        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var t = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(ShowSettings);
        }) { IsBackground = true, Name = "ShowSignal" };
        t.Start();

        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            try
            {
                System.IO.Directory.CreateDirectory(SettingsStore.Dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(SettingsStore.Dir, "error.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.Exception}\n\n");
            }
            catch { }
            if (_errorShown) return;
            _errorShown = true;
            MessageBox.Show("Ошибка: " + ex.Exception.Message + "\n\nПодробности — в error.log в папке настроек.",
                "Crosshair Overlay", MessageBoxButton.OK, MessageBoxImage.Warning);
            _errorShown = false;
        };

        Settings = SettingsStore.Load();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SettingsStore.Save(Settings); };

        Overlay = new OverlayWindow();
        Tracker = new WindowTracker(Settings, Overlay);
        Tracker.GameTick += PollHotkeys;
        Tracker.GameTick += AnimateOverlay;
        Tracker.StateChanged += UpdateTrayTooltip;

        Settings.PropertyChanged += OnSettingsChanged;
        Settings.Presets.CollectionChanged += (_, _) => ScheduleSave();
        SubscribeActive();
        Rerender();
        RebuildHotkeys();

        _tray = new TrayIcon(Overlay);
        _tray.LeftClick += ShowSettings;
        _tray.RightClick += ShowTrayMenu;

        Tracker.Start();

        bool toTray = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        if (!toTray) ShowSettings();
        else { ApplyPriority(); TrimMemorySoon(); }

        CheckNvidia();
    }

    void OnSettingsChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.ActivePreset))
        {
            SubscribeActive();
            Rerender();
            ActivePresetChanged?.Invoke();
        }
        if (e.PropertyName is nameof(AppSettings.Hotkey) or nameof(AppSettings.NextPresetHotkey) or nameof(AppSettings.NextPresetEnabled))
            RebuildHotkeys();
        if (e.PropertyName is nameof(AppSettings.NvidiaAuto) or nameof(AppSettings.TargetProcess)) CheckNvidia();
        if (e.PropertyName == nameof(AppSettings.LowPriority)) ApplyPriority();
        if (e.PropertyName == nameof(AppSettings.Enabled)) Tracker.Invalidate();
        ScheduleSave();
    }

    void SubscribeActive()
    {
        if (_subscribed != null) _subscribed.PropertyChanged -= OnPresetChanged;
        _subscribed = ActivePreset;
        _subscribed.PropertyChanged += OnPresetChanged;
    }

    void OnPresetChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Preset.Hotkey)) HotkeyChanged?.Invoke();
        else if (e.PropertyName != nameof(Preset.Name)) Rerender();
        ScheduleSave();
    }

    public void ScheduleSave() { _saveTimer.Stop(); _saveTimer.Start(); }

    public CrosshairBitmap CurrentBitmap { get; private set; } = new();
    public event Action? Rendered;

    public CrosshairAnimator OverlayAnimator { get; private set; } = null!;
    CrosshairBitmap? _shownFrame;

    public void Rerender()
    {
        OverlayAnimator ??= new CrosshairAnimator(() => ActivePreset);
        OverlayAnimator.Invalidate();
        CurrentBitmap = OverlayAnimator.Static;
        _shownFrame = CurrentBitmap;
        Overlay.SetImage(CurrentBitmap);
        Tracker.Invalidate();
        Rendered?.Invoke();
    }

    void AnimateOverlay()
    {
        if (!OverlayAnimator.IsAnimated) return;
        var frame = OverlayAnimator.Update(WindowTracker.IsFireButtonDown(), Environment.TickCount64);
        if (ReferenceEquals(frame, _shownFrame)) return;
        _shownFrame = frame;
        Overlay.SetImage(frame);
    }

    public void SelectPreset(int index)
    {
        if (index < 0 || index >= Settings.Presets.Count) return;
        if (Settings.ActivePreset == index) { SubscribeActive(); Rerender(); ActivePresetChanged?.Invoke(); }
        else Settings.ActivePreset = index;
    }

    readonly Dictionary<string, Chord?> _chords = new(StringComparer.OrdinalIgnoreCase);

    Chord? GetChord(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!_chords.TryGetValue(text, out var c)) _chords[text] = c = Chord.Parse(text);
        return c;
    }

        public bool HotkeyValid(string text) => string.IsNullOrWhiteSpace(text) || Chord.Parse(text) != null;

    void RebuildHotkeys()
    {
        Tracker.HotkeyOff = false;
        HotkeyChanged?.Invoke();
    }

        void PollHotkeys()
    {
        var entries = new List<(Chord Chord, Action Run)>();
        void Add(string? text, Action run) { var c = GetChord(text); if (c != null) entries.Add((c, run)); }

        Add(Settings.Hotkey, () => Tracker.HotkeyOff = !Tracker.HotkeyOff);
        if (Settings.NextPresetEnabled && Settings.Presets.Count > 1)
            Add(Settings.NextPresetHotkey, () => SelectPreset((Settings.ActivePreset + 1) % Settings.Presets.Count));
        for (int i = 0; i < Settings.Presets.Count; i++)
        {
            int idx = i;
            Add(Settings.Presets[i].Hotkey, () => { Tracker.HotkeyOff = false; SelectPreset(idx); });
        }
        if (entries.Count == 0) return;

        var fired = new Dictionary<Chord, bool>();
        foreach (var e in entries)
            if (!fired.ContainsKey(e.Chord)) fired[e.Chord] = e.Chord.Poll(out _);

        var firedChords = fired.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
        if (firedChords.Count == 0) return;
        foreach (var e in entries)
        {
            if (!fired[e.Chord]) continue;
            bool shadowed = firedChords.Any(o => o.Keys.Count > e.Chord.Keys.Count && o.Contains(e.Chord));
            if (!shadowed) e.Run();
        }
    }

    public string NvidiaStatus { get; private set; } = "Проверка…";
    public bool NvidiaNeedsAdmin { get; private set; }
    public event Action? NvidiaStatusChanged;
    int _nvidiaCheck;

    static IEnumerable<string> TargetExes(string targets) => targets
        .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n : n + ".exe");

        public void CheckNvidia(bool elevate = false)
    {
        int ticket = ++_nvidiaCheck;
        bool auto = Settings.NvidiaAuto;
        var exes = TargetExes(Settings.TargetProcess).ToList();
        var applied = Settings.NvidiaAppliedExes.ToList();
        System.Threading.Tasks.Task.Run(() =>
        {
            string status; bool needAdmin = false; var nowApplied = new List<string>(applied);
            try
            {
                if (!auto)
                {
                    foreach (var exe in applied)
                    {
                        int rr = elevate ? RunElevated("revert", exe) : NvidiaProfile.Revert(exe, out _);
                        if (rr == NvidiaProfile.NVAPI_OK) nowApplied.Remove(exe);
                        else if (rr == NvidiaProfile.NVAPI_INVALID_USER_PRIVILEGE) needAdmin = true;
                    }
                    var st = NvidiaProfile.GetStatus(exes.FirstOrDefault() ?? "stalzonew.exe");
                    status = st.Availability == NvidiaProfile.Availability.NoNvidia
                        ? "Видеокарта не NVIDIA — автонастройка недоступна."
                        : needAdmin ? "Чтобы отменить изменения драйвера, нужны права администратора."
                        : "Автонастройка выключена" + (applied.Count > 0 ? ", изменения драйвера отменены." : ".");
                }
                else
                {
                    var lines = new List<string>();
                    status = "";
                    foreach (var exe in exes)
                    {
                        var st = NvidiaProfile.GetStatus(exe);
                        if (st.Availability == NvidiaProfile.Availability.NoNvidia)
                        {
                            status = "Видеокарта не NVIDIA (AMD/Intel) — эта настройка к ней не относится. Если прицел не виден в полноэкранном режиме, используйте «В окне без рамки».";
                            break;
                        }
                        if (st.Availability == NvidiaProfile.Availability.Error) { lines.Add($"{exe}: ошибка NVAPI ({st.Message})"); continue; }
                        if (st.Value == NvidiaProfile.PreferEnabled)
                        {
                            lines.Add($"{exe}: включено");
                            continue;
                        }
                        int r = elevate ? RunElevated("apply", exe) : NvidiaProfile.Apply(exe, out _);
                        if (r == NvidiaProfile.NVAPI_OK)
                        {
                            if (!nowApplied.Contains(exe, StringComparer.OrdinalIgnoreCase)) nowApplied.Add(exe);
                            lines.Add($"{exe}: включено только что — перезапустите игру");
                        }
                        else if (r == NvidiaProfile.NVAPI_INVALID_USER_PRIVILEGE) { needAdmin = true; lines.Add($"{exe}: нужны права администратора"); }
                        else lines.Add($"{exe}: не удалось (код {r})");
                    }
                    if (status == "") status = "NVIDIA: " + string.Join("; ", lines);
                }
            }
            catch (Exception ex) { status = "NVIDIA: ошибка — " + ex.Message; }

            Dispatcher.BeginInvoke(() =>
            {
                if (ticket != _nvidiaCheck) return;
                NvidiaStatus = status;
                NvidiaNeedsAdmin = needAdmin;
                if (!nowApplied.SequenceEqual(Settings.NvidiaAppliedExes))
                {
                    Settings.NvidiaAppliedExes = nowApplied;
                    ScheduleSave();
                }
                NvidiaStatusChanged?.Invoke();
            });
        });
    }

        static int RunElevated(string action, string exe)
    {
        try
        {
            var outFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"crosshair-nv-{Guid.NewGuid():N}.txt");
            var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = true, Verb = "runas" };
            psi.ArgumentList.Add("--nvidia"); psi.ArgumentList.Add(action); psi.ArgumentList.Add(exe); psi.ArgumentList.Add(outFile);
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit();
            var text = System.IO.File.Exists(outFile) ? System.IO.File.ReadAllText(outFile) : "-1";
            try { System.IO.File.Delete(outFile); } catch { }
            return int.TryParse(text.Split(' ')[0], out int code) ? code : -1;
        }
        catch { return -1; }
    }

        void ApplyPriority() => Native.SetBackgroundMode(Settings.LowPriority && _main == null);

        void TrimMemorySoon()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (_main != null) return;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            Native.EmptyWorkingSet(Native.GetCurrentProcess());
        };
        t.Start();
    }

    void UpdateTrayTooltip()
    {
        _tray?.SetTooltip("Crosshair Overlay — " + StatusText(Tracker.State, Tracker.TargetInfo));
    }

    public static string StatusText(TrackState st, string info) => st switch
    {
        TrackState.Disabled => "прицел выключен",
        TrackState.HotkeyOff => "скрыт горячей клавишей",
        TrackState.NoTarget => "цель не задана",
        TrackState.TargetNotForeground => "ждём, когда окно игры станет активным",
        TrackState.Minimized => "окно игры свёрнуто",
        TrackState.CursorVisible => $"курсор виден (меню) — скрыт · {info}",
        TrackState.Aiming => $"зажата ПКМ — скрыт · {info}",
        TrackState.Shown => $"прицел на экране · {info}",
        _ => "",
    };

    public void ShowSettings()
    {
        if (_main == null)
        {
            _main = new MainWindow();
            _main.Closed += (_, _) => { _main = null; ApplyPriority(); TrimMemorySoon(); };
            ApplyPriority();
        }
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    void ShowTrayMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };

        var enabled = new MenuItem { Header = "Прицел включён", IsCheckable = true, IsChecked = Settings.Enabled };
        enabled.Click += (_, _) => Settings.Enabled = enabled.IsChecked;
        menu.Items.Add(enabled);

        var hide = new MenuItem { Header = "Скрывать при видимом курсоре", IsCheckable = true, IsChecked = Settings.HideWhenCursorVisible };
        hide.Click += (_, _) => Settings.HideWhenCursorVisible = hide.IsChecked;
        menu.Items.Add(hide);

        var presets = new MenuItem { Header = "Пресет" };
        for (int i = 0; i < Settings.Presets.Count; i++)
        {
            int idx = i;
            var mi = new MenuItem { Header = Settings.Presets[i].Name, IsChecked = i == Settings.ActivePreset };
            mi.Click += (_, _) => SelectPreset(idx);
            presets.Items.Add(mi);
        }
        menu.Items.Add(presets);
        menu.Items.Add(new Separator());

        var settings = new MenuItem { Header = "Настройки…" };
        settings.Click += (_, _) => ShowSettings();
        menu.Items.Add(settings);

        var exit = new MenuItem { Header = "Выход" };
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(exit);

        menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(menu) is HwndSource src) Native.SetForegroundWindow(src.Handle);
        };
        menu.IsOpen = true;
    }

    public void ExitApp()
    {
        _saveTimer.Stop();
        SettingsStore.Save(Settings);
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _tray?.Dispose();
            Overlay?.Dispose();
        }
        catch { }
        base.OnExit(e);
    }
}
