using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CrosshairOverlay;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    readonly App _app = App.Current;
    int _zoom = 4;
    readonly CrosshairAnimator _previewAnim;
    readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    bool _previewFiring;
    CrosshairBitmap? _previewShown;
    DispatcherTimer? _pickTimer;
    DateTime _pickDeadline;

    public AppSettings Settings => _app.Settings;
    public Preset ActivePreset => _app.ActivePreset;
    public event PropertyChangedEventHandler? PropertyChanged;

    static readonly string[] FillColors =
    {
        "#FF3DFF7A", "#FF00E5FF", "#FFFF3B3B", "#FFFFD23F", "#FFFF4FD8", "#FFFFFFFF",
        "#FFB7FF00", "#FFFF8A00", "#FF7C5CFF", "#FF00FFB2", "#FF000000",
    };
    static readonly string[] OutlineColors = { "#FF000000", "#FFFFFFFF", "#FF1A1A1A", "#80000000" };

    public MainWindow()
    {
        InitializeComponent();
        _previewAnim = new CrosshairAnimator(() => _app.ActivePreset);
        _previewTimer.Tick += (_, _) => ShowPreview(_previewAnim.Update(_previewFiring, Environment.TickCount64));
        PreviewBox.MouseLeftButtonDown += (_, e) => { _previewFiring = true; PreviewBox.CaptureMouse(); e.Handled = true; };
        PreviewBox.MouseLeftButtonUp += (_, _) => { _previewFiring = false; PreviewBox.ReleaseMouseCapture(); };
        DataContext = this;

        BuildSwatches(FillSwatches, FillColors, c => ActivePreset.Color = c);
        BuildSwatches(OutlineSwatches, OutlineColors, c => ActivePreset.OutlineColor = c);

        PresetList.SelectedIndex = Settings.ActivePreset;
        PixelEditor.Preset = ActivePreset;
        AutostartBox.IsChecked = Autostart.IsEnabled;
        AutostartBox.Checked += Autostart_Changed;
        AutostartBox.Unchecked += Autostart_Changed;
        WatchPreset(ActivePreset);
        PathsText.Text = $"Настройки: {SettingsStore.FilePath}";

        _app.Rendered += UpdatePreview;
        _app.ActivePresetChanged += OnActivePresetChanged;
        _app.Tracker.StateChanged += UpdateStatus;
        _app.HotkeyChanged += UpdateHotkeyStatus;
        _app.NvidiaStatusChanged += UpdateNvidiaStatus;
        Closed += (_, _) =>
        {
            _app.Rendered -= UpdatePreview;
            _app.ActivePresetChanged -= OnActivePresetChanged;
            _app.Tracker.StateChanged -= UpdateStatus;
            _app.HotkeyChanged -= UpdateHotkeyStatus;
            _app.NvidiaStatusChanged -= UpdateNvidiaStatus;
            _pickTimer?.Stop();
        };

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.ApplyDarkTitleBar(h, 0x00130F0E);
            _app.Tracker.RegisterOwnWindow(h);
        };

        UpdatePreview();
        UpdateStatus();
        UpdateHotkeyStatus();
        UpdateNvidiaStatus();
    }

    void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    void OnActivePresetChanged()
    {
        Raise(nameof(ActivePreset));
        PixelEditor.Preset = ActivePreset;
        WatchPreset(ActivePreset);
        if (PresetList.SelectedIndex != Settings.ActivePreset) PresetList.SelectedIndex = Settings.ActivePreset;
    }

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageCrosshair == null) return;
        PageCrosshair.Visibility = NavCrosshair.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageTarget.Visibility = NavTarget.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageBehavior.Visibility = NavBehavior.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdatePreview()
    {
        if (_previewAnim == null || PreviewImage == null) return;
        _previewAnim.Invalidate();
        _previewShown = null;
        ShowPreview(_previewAnim.Update(_previewFiring, Environment.TickCount64));
        _previewTimer.IsEnabled = _previewAnim.IsAnimated;
    }

    void ShowPreview(CrosshairBitmap bmp)
    {
        if (bmp.Width == 0 || ReferenceEquals(bmp, _previewShown)) return;
        _previewShown = bmp;
        PreviewImage.Source = CrosshairRenderer.ToWriteable(bmp);

        var dpi = VisualTreeHelper.GetDpi(this);
        PreviewImage.Width = bmp.Width * _zoom / dpi.DpiScaleX;
        PreviewImage.Height = bmp.Height * _zoom / dpi.DpiScaleY;
        PreviewImage.Stretch = Stretch.Fill;
    }

    void Zoom_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && int.TryParse(fe.Tag?.ToString(), out int z)) { _zoom = z; if (PreviewImage != null) UpdatePreview(); }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdatePreview();
    }

    void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetList.SelectedIndex >= 0 && PresetList.SelectedIndex != Settings.ActivePreset)
            _app.SelectPreset(PresetList.SelectedIndex);
    }

    void AddPreset_Click(object sender, RoutedEventArgs e)
    {
        Settings.Presets.Add(new Preset { Name = $"Прицел {Settings.Presets.Count + 1}" });
        _app.SelectPreset(Settings.Presets.Count - 1);
    }

    void DuplicatePreset_Click(object sender, RoutedEventArgs e)
    {
        var copy = ActivePreset.Clone();
        copy.Name = ActivePreset.Name + " (копия)";
        copy.Hotkey = "";
        Settings.Presets.Insert(Settings.ActivePreset + 1, copy);
        _app.SelectPreset(Settings.ActivePreset + 1);
    }

    void ResetPreset_Click(object sender, RoutedEventArgs e)
    {
        var fresh = Preset.CreateDefault(ActivePreset.DefaultId);
        if (fresh == null)
        {
            MessageBox.Show(this, "Это ваш собственный пресет — у него нет стандартной версии. Сбросить можно только стандартные прицелы.", "Crosshair Overlay");
            return;
        }
        if (MessageBox.Show(this, $"Вернуть пресет «{ActivePreset.Name}» к стандартному виду? Ваши изменения в нём пропадут (клавиша пресета сохранится).",
                "Crosshair Overlay", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        fresh.Hotkey = ActivePreset.Hotkey;
        int i = Settings.ActivePreset;
        Settings.Presets[i] = fresh;
        _app.SelectPreset(i);
    }

    void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        var have = Settings.Presets.Select(p => p.DefaultId).ToHashSet();
        int added = 0;
        foreach (var d in Preset.Defaults())
            if (!have.Contains(d.DefaultId)) { Settings.Presets.Add(d); added++; }
        MessageBox.Show(this, added == 0 ? "Все стандартные прицелы уже на месте." : $"Добавлено стандартных прицелов: {added}.", "Crosshair Overlay");
    }

    void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (Settings.Presets.Count <= 1)
        {
            MessageBox.Show(this, "Нужен хотя бы один пресет.", "Crosshair Overlay");
            return;
        }
        if (MessageBox.Show(this, $"Удалить пресет «{ActivePreset.Name}»?", "Crosshair Overlay",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        int i = Settings.ActivePreset;
        Settings.Presets.RemoveAt(i);
        int next = Math.Min(i, Settings.Presets.Count - 1);
        if (Settings.ActivePreset == next) _app.SelectPreset(next); else Settings.ActivePreset = next;
    }

    void BuildSwatches(Panel host, IEnumerable<string> colors, Action<string> apply)
    {
        foreach (var c in colors)
        {
            var b = new Button
            {
                Style = (Style)FindResource("Swatch"),
                Background = new SolidColorBrush(CrosshairRenderer.ParseColor(c, Colors.White)),
                ToolTip = c,
            };
            b.Click += (_, _) => apply(c);
            host.Children.Add(b);
        }
    }

    void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb)
        {
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }

    void GridSize_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !int.TryParse(fe.Tag?.ToString(), out int ng)) return;
        var p = ActivePreset;
        int og = p.GridSize;
        if (og == ng) return;

        int shift = ng / 2 - og / 2;
        var moved = new Dictionary<int, string>();
        foreach (var kv in p.Pixels)
        {
            int x = kv.Key % og + shift, y = kv.Key / og + shift;
            if (x >= 0 && y >= 0 && x < ng && y < ng) moved[y * ng + x] = kv.Value;
        }
        p.GridSize = ng;
        p.Pixels = moved;
    }

    void Mirror_Click(object sender, RoutedEventArgs e)
    {
        PixelEditor.MirrorX = MirrorXBox.IsChecked == true;
        PixelEditor.MirrorY = MirrorYBox.IsChecked == true;
    }

    void ClearPixels_Click(object sender, RoutedEventArgs e) => ActivePreset.Pixels = new Dictionary<int, string>();

    void ShapesToPixels_Click(object sender, RoutedEventArgs e)
    {
        var p = ActivePreset;
        var shapes = p.Clone();
        shapes.Mode = CrosshairMode.Shapes;
        shapes.OutlineEnabled = false;
        shapes.Opacity = 1;
        var bmp = CrosshairRenderer.Render(shapes);
        int g = p.GridSize, half = g / 2;
        var px = new Dictionary<int, string>();
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                uint v = (uint)bmp.Pixels[y * bmp.Width + x];
                byte a = (byte)(v >> 24);
                if (a < 128) continue;
                int gx = x - bmp.CenterX + half, gy = y - bmp.CenterY + half;
                if (gx < 0 || gy < 0 || gx >= g || gy >= g) continue;

                byte r = (byte)Math.Min(255, ((v >> 16) & 0xFF) * 255 / a);
                byte gg = (byte)Math.Min(255, ((v >> 8) & 0xFF) * 255 / a);
                byte b = (byte)Math.Min(255, (v & 0xFF) * 255 / a);
                px[gy * g + gx] = CrosshairRenderer.Hex(255, r, gg, b);
            }
        p.Pixels = px;
    }

    void LoadImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Картинка прицела",
            Filter = "Прицелы (*.png;*.svg;*.gif)|*.png;*.svg;*.gif|PNG (*.png)|*.png|SVG (*.svg)|*.svg|GIF-анимация (*.gif)|*.gif|Все изображения|*.png;*.svg;*.gif;*.bmp;*.jpg;*.jpeg",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            Directory.CreateDirectory(SettingsStore.ImagesDir);
            var name = $"{Path.GetFileNameWithoutExtension(dlg.FileName)}-{DateTime.Now:yyyyMMddHHmmss}{Path.GetExtension(dlg.FileName)}";
            var dest = Path.Combine(SettingsStore.ImagesDir, name);
            File.Copy(dlg.FileName, dest, true);
            ActivePreset.ImagePath = dest;
            ActivePreset.Mode = CrosshairMode.Image;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось загрузить картинку: " + ex.Message, "Crosshair Overlay");
        }
    }

    void RefreshWindows_Click(object sender, RoutedEventArgs e)
    {
        var self = new WindowInteropHelper(this).Handle;
        WindowList.ItemsSource = WindowTracker.ListWindows(new[] { self, _app.Overlay.Handle });
        WindowListPanel.Visibility = Visibility.Visible;
    }

    void WindowList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (WindowList.SelectedItem is WindowInfo wi) UseWindow(wi.Handle);
    }

    void DefaultTarget_Click(object sender, RoutedEventArgs e)
    {
        Settings.TargetProcess = AppSettings.DefaultTarget;
        Settings.TargetTitle = "";
    }

    void PickByClick_Click(object sender, RoutedEventArgs e)
    {
        var self = new WindowInteropHelper(this).Handle;
        _pickDeadline = DateTime.Now.AddSeconds(10);
        PickHint.Visibility = Visibility.Visible;
        _pickTimer?.Stop();
        _pickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _pickTimer.Tick += (_, _) =>
        {
            var fg = Native.GetForegroundWindow();
            var left = (_pickDeadline - DateTime.Now).TotalSeconds;
            if (fg != IntPtr.Zero && fg != self && fg != _app.Overlay.Handle && Native.IsWindowVisible(fg) && !IsOwnPopup(fg))
            {
                _pickTimer!.Stop();
                UseWindow(fg);
                PickHint.Text = $"Выбрано: {Settings.TargetProcess}";
                Activate();
                return;
            }
            if (left <= 0)
            {
                _pickTimer!.Stop();
                PickHint.Text = "Время вышло — окно не выбрано.";
                return;
            }
            PickHint.Text = $"Щёлкните по окну игры… ({Math.Ceiling(left)} с)";
        };
        _pickTimer.Start();
        PickHint.Text = "Щёлкните по окну игры… (10 с)";
    }

    static bool IsOwnPopup(IntPtr h)
    {
        Native.GetWindowThreadProcessId(h, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    void UseWindow(IntPtr h)
    {
        Native.GetWindowThreadProcessId(h, out uint pid);
        var exe = ProcessNames.Get(pid);
        if (string.IsNullOrEmpty(exe)) return;
        Settings.TargetProcess = exe;
        Settings.TargetTitle = "";
        _app.Tracker.Invalidate();
    }

    void UpdateStatus()
    {
        var st = _app.Tracker.State;
        var text = App.StatusText(st, _app.Tracker.TargetInfo);
        var brush = (Brush)FindResource(st switch
        {
            TrackState.Shown => "Ok",
            TrackState.CursorVisible or TrackState.HotkeyOff or TrackState.Aiming => "Accent",
            TrackState.Disabled => "Warn",
            _ => "TextDim",
        });
        StatusText.Text = char.ToUpper(text[0]) + text[1..];
        StatusText2.Text = StatusText.Text;
        StatusDot.Fill = brush;
        StatusDot2.Fill = brush;
    }

    void ClearHotkey_Click(object sender, RoutedEventArgs e) => Settings.Hotkey = "";
    void ClearNextHotkey_Click(object sender, RoutedEventArgs e) => Settings.NextPresetHotkey = "";

    void UpdateHotkeyStatus()
    {
        var parts = new List<string>();
        bool bad = false;
        if (!_app.HotkeyValid(Settings.Hotkey)) { parts.Add($"«{Settings.Hotkey}» не распознано"); bad = true; }
        else if (!string.IsNullOrWhiteSpace(Settings.Hotkey)) parts.Add($"{Settings.Hotkey} — скрыть/показать");
        if (Settings.NextPresetEnabled)
        {
            if (string.IsNullOrWhiteSpace(Settings.NextPresetHotkey)) parts.Add("задайте сочетание для смены пресета");
            else if (!_app.HotkeyValid(Settings.NextPresetHotkey)) { parts.Add($"«{Settings.NextPresetHotkey}» не распознано"); bad = true; }
            else parts.Add($"{Settings.NextPresetHotkey} — следующий пресет");
        }
        int own = Settings.Presets.Count(p => !string.IsNullOrWhiteSpace(p.Hotkey));
        foreach (var p in Settings.Presets.Where(p => !_app.HotkeyValid(p.Hotkey)))
        { parts.Add($"«{p.Hotkey}» у пресета «{p.Name}» не распознано"); bad = true; }
        if (own > 0) parts.Add($"своя клавиша у {own} из {Settings.Presets.Count} пресетов");
        HotkeyStatus.Text = parts.Count == 0 ? "Горячие клавиши не заданы." : string.Join(" · ", parts);
        HotkeyStatus.Foreground = (Brush)FindResource(bad ? "Warn" : "Accent");
    }

    void UpdateNvidiaStatus()
    {
        NvidiaStatusText.Text = _app.NvidiaStatus;
        NvidiaAdminButton.Visibility = _app.NvidiaNeedsAdmin ? Visibility.Visible : Visibility.Collapsed;
    }

    void NvidiaAdmin_Click(object sender, RoutedEventArgs e) => _app.CheckNvidia(elevate: true);

    bool _autostartBusy;
    void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (_autostartBusy) return;
        _autostartBusy = true;
        try { Autostart.Set(AutostartBox.IsChecked == true); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Crosshair Overlay"); }
        AutostartBox.IsChecked = Autostart.IsEnabled;
        _autostartBusy = false;
    }

    Preset? _watched;
    void WatchPreset(Preset p)
    {
        if (_watched != null) _watched.PropertyChanged -= Watched_PropertyChanged;
        _watched = p;
        p.PropertyChanged += Watched_PropertyChanged;
    }

    void Watched_PropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Preset.Mode) && _watched is { Mode: CrosshairMode.Pixels, Pixels.Count: 0 })
            ShapesToPixels_Click(this, new RoutedEventArgs());
    }
}
