using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrosshairOverlay;

public enum CrosshairMode { Shapes, Pixels, Image }

public enum GifPlayback { Always, WhileFiring, OnClick }

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class Preset : Observable
{
    string _name = "Новый прицел";
    CrosshairMode _mode = CrosshairMode.Shapes;

    bool _dotEnabled = true; int _dotSize = 2; bool _dotRound;
    bool _crossEnabled = true; int _crossLength = 6; int _crossThickness = 2; int _crossGap = 4; bool _tStyle;
    bool _circleEnabled; int _circleRadius = 12; int _circleThickness = 1;

    string _color = "#FF3DFF7A";
    bool _outlineEnabled = true; int _outlineThickness = 1; string _outlineColor = "#FF000000";
    double _opacity = 1.0;
    int _offsetX, _offsetY;

    int _gridSize = 21; int _pixelScale = 1;
    Dictionary<int, string> _pixels = new();

    string? _imagePath; int _imageSize = 32;
    string _hotkey = "";
    string? _defaultId;
    GifPlayback _gifPlayback = GifPlayback.Always;
    bool _spreadEnabled; int _spreadAmount = 6; int _spreadRiseMs = 150; int _spreadRecoverMs = 300;
    bool _fireColorEnabled; string _fireColor = "#FFFF3B3B";

    public string Name { get => _name; set => Set(ref _name, value); }
    public CrosshairMode Mode { get => _mode; set => Set(ref _mode, value); }

    public bool DotEnabled { get => _dotEnabled; set => Set(ref _dotEnabled, value); }
    public int DotSize { get => _dotSize; set => Set(ref _dotSize, value); }
    public bool DotRound { get => _dotRound; set => Set(ref _dotRound, value); }

    public bool CrossEnabled { get => _crossEnabled; set => Set(ref _crossEnabled, value); }
    public int CrossLength { get => _crossLength; set => Set(ref _crossLength, value); }
    public int CrossThickness { get => _crossThickness; set => Set(ref _crossThickness, value); }
    public int CrossGap { get => _crossGap; set => Set(ref _crossGap, value); }
        public bool TStyle { get => _tStyle; set => Set(ref _tStyle, value); }

    public bool CircleEnabled { get => _circleEnabled; set => Set(ref _circleEnabled, value); }
    public int CircleRadius { get => _circleRadius; set => Set(ref _circleRadius, value); }
    public int CircleThickness { get => _circleThickness; set => Set(ref _circleThickness, value); }

    public string Color { get => _color; set => Set(ref _color, value); }
    public bool OutlineEnabled { get => _outlineEnabled; set => Set(ref _outlineEnabled, value); }
    public int OutlineThickness { get => _outlineThickness; set => Set(ref _outlineThickness, value); }
    public string OutlineColor { get => _outlineColor; set => Set(ref _outlineColor, value); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, 0.05, 1.0)); }
    public int OffsetX { get => _offsetX; set => Set(ref _offsetX, value); }
    public int OffsetY { get => _offsetY; set => Set(ref _offsetY, value); }

    public int GridSize { get => _gridSize; set => Set(ref _gridSize, value); }
    public int PixelScale { get => _pixelScale; set => Set(ref _pixelScale, value); }
        public Dictionary<int, string> Pixels { get => _pixels; set { _pixels = value ?? new(); Raise(nameof(Pixels)); } }
    public void NotifyPixelsChanged() => Raise(nameof(Pixels));

    public string? ImagePath { get => _imagePath; set => Set(ref _imagePath, value); }
        public string Hotkey { get => _hotkey; set => Set(ref _hotkey, value ?? ""); }
    public int ImageSize { get => _imageSize; set => Set(ref _imageSize, value); }

    public string? DefaultId { get => _defaultId; set => Set(ref _defaultId, value); }
    public GifPlayback GifPlayback { get => _gifPlayback; set => Set(ref _gifPlayback, value); }
    public bool SpreadEnabled { get => _spreadEnabled; set => Set(ref _spreadEnabled, value); }
    public int SpreadAmount { get => _spreadAmount; set => Set(ref _spreadAmount, value); }
    public int SpreadRiseMs { get => _spreadRiseMs; set => Set(ref _spreadRiseMs, value); }
    public int SpreadRecoverMs { get => _spreadRecoverMs; set => Set(ref _spreadRecoverMs, value); }
    public bool FireColorEnabled { get => _fireColorEnabled; set => Set(ref _fireColorEnabled, value); }
    public string FireColor { get => _fireColor; set => Set(ref _fireColor, value); }

    [JsonIgnore]
    public bool IsDynamic => Mode == CrosshairMode.Shapes && ((SpreadEnabled && SpreadAmount > 0) || FireColorEnabled);

    public Preset Clone()
    {
        var json = JsonSerializer.Serialize(this, SettingsStore.JsonOptions);
        return JsonSerializer.Deserialize<Preset>(json, SettingsStore.JsonOptions)!;
    }

    public override string ToString() => Name;

    public static Preset? CreateDefault(string? id) => id == null ? null : Defaults().FirstOrDefault(d => d.DefaultId == id);

    public static IEnumerable<Preset> Defaults()
    {
        yield return new Preset { Name = "Классический крест", DefaultId = "classic" };
        yield return new Preset
        {
            Name = "Точка", DefaultId = "dot", CrossEnabled = false, DotSize = 4, DotRound = true, Color = "#FFFF3B3B",
        };
        yield return new Preset
        {
            Name = "T-образный", DefaultId = "t", TStyle = true, DotEnabled = false, CrossGap = 3, CrossLength = 7,
            CrossThickness = 1, Color = "#FF00E5FF",
        };
        yield return new Preset
        {
            Name = "Круг с точкой", DefaultId = "circle", CrossEnabled = false, CircleEnabled = true, CircleRadius = 10,
            DotSize = 2, DotRound = true, Color = "#FFFFD23F",
        };
        yield return new Preset
        {
            Name = "Динамический разлёт", DefaultId = "spread", DotEnabled = true, DotSize = 2, CrossGap = 3, CrossLength = 6,
            CrossThickness = 2, Color = "#FF3DFF7A", SpreadEnabled = true, SpreadAmount = 7, SpreadRiseMs = 140, SpreadRecoverMs = 260,
            FireColorEnabled = true, FireColor = "#FFFF5A3B",
        };
    }
}

public sealed class AppSettings : Observable
{
    bool _enabled = true;
    string _targetProcess = DefaultTarget;
    public const string DefaultTarget = "stalzonew.exe";
    string _targetTitle = "";
    bool _hideWhenCursorVisible = true;
    string _hotkey = "Ctrl+Alt+X";
    int _activePreset;
    bool _hideWhileAiming;
    bool _nextPresetEnabled;
    string _nextPresetHotkey = "";
    bool _nvidiaAuto = true;
    bool _lowPriority = true;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
        public string TargetProcess { get => _targetProcess; set => Set(ref _targetProcess, value ?? ""); }
        public string TargetTitle { get => _targetTitle; set => Set(ref _targetTitle, value ?? ""); }
    public bool HideWhenCursorVisible { get => _hideWhenCursorVisible; set => Set(ref _hideWhenCursorVisible, value); }
    public string Hotkey { get => _hotkey; set => Set(ref _hotkey, value ?? ""); }
    public int ActivePreset { get => _activePreset; set => Set(ref _activePreset, value); }
        public bool HideWhileAiming { get => _hideWhileAiming; set => Set(ref _hideWhileAiming, value); }
    public bool NextPresetEnabled { get => _nextPresetEnabled; set => Set(ref _nextPresetEnabled, value); }
        public string NextPresetHotkey { get => _nextPresetHotkey; set => Set(ref _nextPresetHotkey, value ?? ""); }
        public bool NvidiaAuto { get => _nvidiaAuto; set => Set(ref _nvidiaAuto, value); }
        public bool LowPriority { get => _lowPriority; set => Set(ref _lowPriority, value); }
        public List<string> NvidiaAppliedExes { get; set; } = new();
    public ObservableCollection<Preset> Presets { get; set; } = new();
}

public static class SettingsStore
{
    public static readonly string Dir = Environment.GetEnvironmentVariable("CROSSHAIR_OVERLAY_HOME") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CrosshairOverlay");
    public static readonly string FilePath = Path.Combine(Dir, "settings.json");
    public static readonly string ImagesDir = Path.Combine(Dir, "images");

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        AppSettings? s = null;
        try
        {
            if (File.Exists(FilePath))
                s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
        }
        catch
        {
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch { }
        }
        s ??= new AppSettings();

        bool migrated = string.Equals(s.TargetProcess.Trim(), "stalzone.exe", StringComparison.OrdinalIgnoreCase);
        if (migrated) s.TargetProcess = AppSettings.DefaultTarget;
        bool seeded = s.Presets.Count == 0;
        bool tagged = false;
        foreach (var p in s.Presets.Where(p => p.DefaultId == null))
        {
            p.DefaultId = Preset.Defaults().FirstOrDefault(d => d.Name == p.Name)?.DefaultId;
            tagged |= p.DefaultId != null;
        }
        if (seeded)
            foreach (var p in Preset.Defaults()) s.Presets.Add(p);
        s.ActivePreset = Math.Clamp(s.ActivePreset, 0, s.Presets.Count - 1);
        if (seeded || migrated || tagged) Save(s);
        return s;
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, JsonOptions));
            File.Move(tmp, FilePath, true);
        }
        catch {  }
    }
}
