using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
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
    nint _bgBrush;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    [DllImport("gdi32.dll")] static extern nint CreateSolidBrush(uint colorref);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint hObject);
    [DllImport("user32.dll")] static extern nint SetClassLongPtrW(nint hWnd, int nIndex, nint dwNewLong);
    [DllImport("user32.dll")] static extern bool GetClientRect(nint hWnd, out RECT lpRect);
    [DllImport("user32.dll")] static extern int FillRect(nint hDC, ref RECT lprc, nint hbr);
    [DllImport("user32.dll")] static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint hWnd, nint hDC);

    const int GCLP_HBRBACKGROUND = -10;
    const int DWMWA_CLOAK = 13;
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_BORDER_COLOR = 34;
    const int DWMWA_CAPTION_COLOR = 35;
    const uint WM_ERASEBKGND = 0x0014;

    /// <summary>
    /// Убирает белое мигание Win32-окна при запуске: задаёт кисть фона класса окна в цвет темы,
    /// перехватывает WM_ERASEBKGND и временно скрывает окно через DWM Cloak до отрисовки первого кадра Avalonia.
    /// </summary>
    void InitNativeBackdrop()
    {
        if (TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == 0) return;

        UpdateNativeBackdrop();
        Win32Properties.AddWndProcHookCallback(this, OnWndProc);

        int cloak = 1;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));

        Opened += async (_, _) =>
        {
            try
            {
                if (ElementComposition.GetElementVisual(this)?.Compositor is { } comp)
                {
                    var batch = comp.RequestCompositionBatchCommitAsync();
                    await Task.WhenAny(batch.Rendered, Task.Delay(200));
                }
            }
            catch { }

            int uncloak = 0;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref uncloak, sizeof(int));
            Activate();
            if (_tab != null) Editor.Focus();
        };

        Closed += (_, _) =>
        {
            if (_bgBrush != 0)
            {
                DeleteObject(_bgBrush);
                _bgBrush = 0;
            }
        };
    }

    /// <summary>Обновляет системный фон Win32-окна и атрибуты DWM при запуске и смене темы.</summary>
    void UpdateNativeBackdrop()
    {
        if (TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == 0) return;

        var c = Skin.Bg.Color;
        int colorref = c.R | (c.G << 8) | (c.B << 16);
        int dark = Skin.IsLight ? 0 : 1;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorref, sizeof(int));

        var lc = Skin.Line.Color;
        int borderColorref = lc.R | (lc.G << 8) | (lc.B << 16);
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColorref, sizeof(int));

        nint newBrush = CreateSolidBrush((uint)colorref);
        nint oldBrush = _bgBrush;
        _bgBrush = newBrush;
        if (newBrush != 0)
        {
            SetClassLongPtrW(hwnd, GCLP_HBRBACKGROUND, newBrush);
            nint hdc = GetDC(hwnd);
            if (hdc != 0)
            {
                if (GetClientRect(hwnd, out var rc))
                    FillRect(hdc, ref rc, newBrush);
                ReleaseDC(hwnd, hdc);
            }
        }
        if (oldBrush != 0) DeleteObject(oldBrush);
    }

    nint OnWndProc(nint hWnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_ERASEBKGND && _bgBrush != 0 && wParam != 0)
        {
            if (GetClientRect(hWnd, out var rc))
                FillRect(wParam, ref rc, _bgBrush);
            handled = true;
            return 1;
        }
        return 0;
    }

    static WindowIcon AppIcon() => new(AssetLoader.Open(new Uri($"avares://{typeof(MainWindow).Assembly.GetName().Name}/Assets/leaflet.png")));

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
