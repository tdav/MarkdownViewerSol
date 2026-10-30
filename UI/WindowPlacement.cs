using System.Runtime.InteropServices;

namespace Leaflet.UI;

/// <summary>Прямоугольник в пикселях экрана.</summary>
public readonly record struct Area(int L, int T, int R, int B)
{
    public int W => R - L;
    public int H => B - T;
    public bool Has(int x, int y) => x >= L && x < R && y >= T && y < B;
    public long Overlap(Area o) => (long)Math.Max(0, Math.Min(R, o.R) - Math.Max(L, o.L)) * Math.Max(0, Math.Min(B, o.B) - Math.Max(T, o.T));
}

/// <summary>Монитор: весь экран, рабочая область (без панели задач) и масштаб (1,5 — 150 %).</summary>
public sealed record Mon(Area Bounds, Area Work, double Scale, bool Primary);

/// <summary>Сохранённое положение: X, Y — пиксели экрана (левый верхний угол), W, H — размер в логических единицах (не зависит от масштаба),
/// Scale — масштаб монитора, на котором окно стояло (по нему известен размер окна в пикселях; null — по монитору с точкой X, Y).</summary>
public sealed record Saved(int? X, int? Y, double W, double H, bool Max, int? MaxX, int? MaxY, double? Scale = null);

/// <summary>Куда поставить окно (X, Y пусты — по центру главного монитора) и какого размера.</summary>
public sealed record Placed(int? X, int? Y, double W, double H, bool Max);

/// <summary>
/// Положение окна между запусками. Окно сохраняется на мониторе, которого уже может не быть, или на мониторе с другим
/// масштабом: <see cref="Fit"/> возвращает окно в видимую область, не меняя размер без нужды.
/// </summary>
public static class WindowPlacement
{
    public const double MinW = 640, MinH = 400;

    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorProc proc, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoW(nint mon, ref MONITORINFO info);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(nint mon, int type, out uint x, out uint y);
    delegate bool MonitorProc(nint mon, nint dc, ref RECT rect, nint data);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }

    /// <summary>Подключённые мониторы (пусто, если спросить нельзя — тогда положение не трогаем).</summary>
    public static List<Mon> Monitors()
    {
        var list = new List<Mon>();
        try
        {
            EnumDisplayMonitors(0, 0, (nint m, nint dc, ref RECT r, nint d) =>
            {
                var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfoW(m, ref info)) return true;
                double scale = GetDpiForMonitor(m, 0, out uint dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
                list.Add(new Mon(new Area(info.Monitor.L, info.Monitor.T, info.Monitor.R, info.Monitor.B),
                    new Area(info.Work.L, info.Work.T, info.Work.R, info.Work.B), scale, (info.Flags & 1) != 0));
                return true;
            }, 0);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        return list;
    }

    /// <summary>Ставит сохранённое окно на подходящий монитор; если он пропал, окно уходит на главный.</summary>
    public static Placed Fit(Saved s, IReadOnlyList<Mon> mons)
    {
        if (mons.Count == 0) return new Placed(s.X, s.Y, s.W, s.H, s.Max);
        var primary = mons.FirstOrDefault(m => m.Primary) ?? mons[0];
        if (s.X is not { } x || s.Y is not { } y) return Centered(s, primary);

        // Монитор, где окно видно лучше всего (по площади). Размер окна в пикселях — по масштабу монитора, где оно стояло.
        double was = s.Scale is > 0 ? s.Scale.Value : (mons.FirstOrDefault(m => m.Bounds.Has(x, y)) ?? primary).Scale;
        Mon? best = null;
        long bestArea = 0;
        foreach (var m in mons)
        {
            var win = Rect(x, y, s, was);
            long a = win.Overlap(m.Work);
            if (a > bestArea && TitleVisible(win, m)) { best = m; bestArea = a; }
        }
        if (best == null) return Centered(s, primary);

        // Окно было развёрнуто на другом мониторе, чем лежит его обычное положение: обычное тоже переносим туда.
        if (s.Max && s.MaxX is { } mx && s.MaxY is { } my && mons.FirstOrDefault(m => m.Bounds.Has(mx, my)) is { } where && where != best)
            return Centered(s, where);

        return Inside(s, best, x, y);
    }

    static Area Rect(int x, int y, Saved s, double scale) => new(x, y, x + (int)Math.Round(s.W * scale), y + (int)Math.Round(s.H * scale));

    /// <summary>Полоса заголовка (верхние 32 логических пикселя) хотя бы на 100 пикселей лежит в рабочей области — окно можно схватить.</summary>
    static bool TitleVisible(Area win, Mon m)
    {
        var strip = new Area(win.L, win.T, win.R, win.T + (int)(32 * m.Scale));
        int w = Math.Min(win.R, m.Work.R) - Math.Max(win.L, m.Work.L);
        int h = Math.Min(strip.B, m.Work.B) - Math.Max(strip.T, m.Work.T);
        return w >= Math.Min(100 * m.Scale, win.W) && h >= 16 * m.Scale;
    }

    /// <summary>Окно целиком в рабочей области монитора: размер ужимается, если не помещается, положение сдвигается внутрь.</summary>
    static Placed Inside(Saved s, Mon m, int x, int y)
    {
        var (w, h) = Clamp(s, m);
        int pw = (int)Math.Round(w * m.Scale), ph = (int)Math.Round(h * m.Scale);
        x = Math.Clamp(x, m.Work.L, Math.Max(m.Work.L, m.Work.R - pw));
        y = Math.Clamp(y, m.Work.T, Math.Max(m.Work.T, m.Work.B - ph));
        return new Placed(x, y, w, h, s.Max);
    }

    static Placed Centered(Saved s, Mon m)
    {
        var (w, h) = Clamp(s, m);
        int pw = (int)Math.Round(w * m.Scale), ph = (int)Math.Round(h * m.Scale);
        return new Placed(m.Work.L + Math.Max(0, (m.Work.W - pw) / 2), m.Work.T + Math.Max(0, (m.Work.H - ph) / 2), w, h, s.Max);
    }

    /// <summary>Размер в логических пикселях не больше рабочей области монитора (но не меньше минимума окна, если экран совсем мал).</summary>
    static (double, double) Clamp(Saved s, Mon m) =>
        (Math.Max(MinW, Math.Min(s.W, Math.Floor(m.Work.W / m.Scale))), Math.Max(MinH, Math.Min(s.H, Math.Floor(m.Work.H / m.Scale))));
}
