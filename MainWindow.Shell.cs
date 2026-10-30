using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Leaflet.Core;
using Leaflet.UI;

namespace Leaflet;

/// <summary>Окно в системе: значок, положение между запусками, пути из командной строки и от повторного запуска, «Открыть с помощью».</summary>
public sealed partial class MainWindow
{
    // Положение и размер «обычного» окна (не развёрнутого): запоминаются спустя паузу после движения, пока окно в обычном состоянии.
    // Прочитать их в момент закрытия нельзя: развёрнутое окно отдаёт границы монитора.
    DispatcherTimer? _placeTimer;
    PixelPoint _normalPos;
    double _normalW, _normalH, _normalScale = 1;
    bool _lastMax;

    static WindowIcon AppIcon() => new(AssetLoader.Open(new Uri("avares://Leaflet/Assets/leaflet.png")));

    int? SavedInt(string key) => int.TryParse(_settings[key], out var v) ? v : null;
    double? SavedScale() => _settings.Number("scale", 0) is var v and > 0 ? v : null;

    void RestorePlacement()
    {
        var saved = new Saved(SavedInt("x"), SavedInt("y"), _settings.Number("w", 1240), _settings.Number("h", 800),
            _settings["max"] == "1", SavedInt("maxx"), SavedInt("maxy"), SavedScale());
        var p = WindowPlacement.Fit(saved, WindowPlacement.Monitors());
        Width = p.W;
        Height = p.H;
        if (p.X is { } x && p.Y is { } y)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
            _normalPos = Position;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _normalW = p.W;
        _normalH = p.H;
        _normalScale = (WindowPlacement.Monitors().FirstOrDefault(m => p.X is { } px && p.Y is { } py && m.Bounds.Has(px, py)))?.Scale ?? 1;
        _lastMax = p.Max;
        if (p.Max) WindowState = WindowState.Maximized;

        _placeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _placeTimer.Tick += (_, _) => { _placeTimer.Stop(); NoteBounds(); };
        PositionChanged += (_, _) => { _placeTimer.Stop(); _placeTimer.Start(); };
        SizeChanged += (_, _) => { _placeTimer.Stop(); _placeTimer.Start(); };
        Opened += (_, _) => NoteBounds();
    }

    void NoteBounds()
    {
        if (WindowState == WindowState.Normal) { _normalPos = Position; _normalW = Width; _normalH = Height; _normalScale = RenderScaling; _lastMax = false; }
        else if (WindowState == WindowState.Maximized) _lastMax = true;
    }

    void SavePlacement()
    {
        NoteBounds();
        bool max = WindowState == WindowState.Maximized || (WindowState == WindowState.Minimized && _lastMax);
        _settings["max"] = max ? "1" : "0";
        if (WindowState == WindowState.Normal || _normalW > 0)
        {
            _settings["x"] = _normalPos.X.ToString();
            _settings["y"] = _normalPos.Y.ToString();
            _settings["w"] = ((int)_normalW).ToString();
            _settings["h"] = ((int)_normalH).ToString();
            _settings["scale"] = _normalScale.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        // На каком мониторе окно было развёрнуто: точка у левого верхнего угла окна лежит внутри этого монитора.
        if (max) { _settings["maxx"] = (Position.X + 50).ToString(); _settings["maxy"] = (Position.Y + 50).ToString(); }
        else { _settings["maxx"] = null; _settings["maxy"] = null; }
    }

    // ---------- пути извне ----------

    /// <summary>Файл или папка из командной строки повторного запуска (или вторые и следующие пути первого запуска).</summary>
    public void OpenExternal(IReadOnlyList<string> paths, bool raise = true)
    {
        if (raise) BringToFront();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                if (_vault == null || !SamePath(_vault.Root, p)) SetVault(p);
            }
            else if (File.Exists(p) && p.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                if (_vault != null && IsUnder(_vault.Root, p))
                {
                    if ((_vault.Get(p) ?? _vault.Add(p)) is { } note) Open(note, newTab: true);
                }
                else SetVault(Path.GetDirectoryName(p)!, p);
            }
            else { _notice = Loc.T("shell.nofile") + p; UpdateStatus(); }
        }
    }

    static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    static bool IsUnder(string root, string path) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase);

    void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = _lastMax ? WindowState.Maximized : WindowState.Normal;
        Show();
        Activate();
        // Windows не пускает окно вперёд из фонового процесса: короткий Topmost обходит это для окна, которое пользователь сам просит открыть.
        Topmost = true;
        Topmost = false;
        Activate();
    }

    // ---------- «Открыть с помощью» ----------

    void Association(bool register)
    {
        try
        {
            if (register) FileAssoc.Register(FileAssoc.ExePath); else FileAssoc.Unregister();
            _notice = register
                ? Loc.T("shell.registered")
                : Loc.T("shell.unregistered");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _notice = Loc.T("shell.reg.fail") + e.Message;
        }
        UpdateStatus();
    }
}
