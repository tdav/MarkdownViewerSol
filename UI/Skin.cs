using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Leaflet;

public enum ThemeKind { Graphite, Terminal, Light }

/// <summary>
/// Темы «Графит», «Терминал» (макеты 1 и 3) и «Светлая» (вёрстка Графита, светлая палитра). Кисти общие и изменяемые: смена темы меняет их цвет,
/// интерфейс перерисовывается сам, редактору нужен <c>TextView.Redraw()</c>.
/// </summary>
static class Skin
{
    public static ThemeKind Kind { get; private set; }
    public static bool IsTerminal => Kind == ThemeKind.Terminal;
    public static bool IsLight => Kind == ThemeKind.Light;

    /// <summary>Имя темы в settings.ini.</summary>
    public static string Name(ThemeKind kind) => kind switch { ThemeKind.Terminal => "terminal", ThemeKind.Light => "light", _ => "graphite" };
    public static ThemeKind FromName(string? name) => name switch { "terminal" => ThemeKind.Terminal, "light" => ThemeKind.Light, _ => ThemeKind.Graphite };
    /// <summary>Следующая тема по кругу: Графит → Терминал → Светлая.</summary>
    public static ThemeKind Next(ThemeKind kind) => (ThemeKind)(((int)kind + 1) % 3);

    public static readonly SolidColorBrush
        Bg = new(), Panel = new(), Panel2 = new(), Line = new(), Fg = new(), Mute = new(),
        Accent = new(), AccentBack = new(), AccentFg = new(), Sel = new(), CodeBack = new(),
        Heading = new(), Markup = new(), QuoteText = new(), Highlight = new(), Selection = new(), Hover = new(), PickBack = new(),
        StatusBack = new(), StatusFg = new(), Found = new(), FoundNow = new(), DiffAdd = new(), DiffDel = new(), DiffAddFg = new(), DiffDelFg = new(),
        HelpBack = new(), HelpBorder = new(), HelpFg = new();

    /// <summary>Интерфейс и текст заметок: Графит — системный Segoe UI, Терминал — Consolas (запасной шрифт макета 3; JetBrains Mono не встраиваем).</summary>
    public static readonly FontFamily GraphiteFont = new("Segoe UI"), TerminalFont = new("Consolas");
    public static FontFamily Font { get; private set; } = GraphiteFont;
    /// <summary>Шрифт и размер, выбранные в настройках (null и 0 — как в теме). Шрифт — только для текста заметок.</summary>
    public static string? UserFont { get; private set; }
    public static double UserSize { get; private set; }
    public static FontFamily EditorFont { get; private set; } = GraphiteFont;
    /// <summary>Размер текста заметки: в Терминале мельче (макет: 13,5 против 15 px).</summary>
    public static double EditorSize { get; private set; } = 16;
    /// <summary>Размер текста заметки в текущей теме без настроек.</summary>
    public static double ThemeSize => IsTerminal ? 14.5 : 16;
    public static readonly Typeface Symbols = new(new FontFamily("Segoe UI Symbol, Segoe UI"));
    public static readonly Typeface Mono = new(new FontFamily("Cascadia Mono, Consolas, Courier New"));
    public static double MonoSize { get; private set; } = 14.5;
    static readonly double[] GraphiteHeadings = [16, 28, 23, 20, 18, 17, 16], TerminalHeadings = [14.5, 19, 17, 16, 15, 14.5, 14.5];
    public static double[] HeadingSize { get; private set; } = GraphiteHeadings;

    public static event Action? Changed;

    static Skin() => Apply(ThemeKind.Graphite);

    /// <summary>Запоминает шрифт и размер из настроек; применяются со следующим <see cref="Apply"/>.</summary>
    public static void SetText(string? font, double size) { UserFont = font; UserSize = size; }

    public static void Apply(ThemeKind kind)
    {
        Kind = kind;
        if (Application.Current is { } app) app.RequestedThemeVariant = kind == ThemeKind.Light ? ThemeVariant.Light : ThemeVariant.Dark;
        bool term = kind == ThemeKind.Terminal;
        Font = term ? TerminalFont : GraphiteFont;
        EditorFont = UserFont != null ? new FontFamily(UserFont) : Font;
        EditorSize = UserSize > 0 ? UserSize : ThemeSize;
        double k = EditorSize / ThemeSize; // заголовки и код растут вместе с текстом
        MonoSize = (term ? 13 : 14.5) * k;
        HeadingSize = (term ? TerminalHeadings : GraphiteHeadings).Select(h => h * k).ToArray();
        DiffAddFg.Color = C(0x7ad68f);
        DiffDelFg.Color = C(0xf08a8a);
        if (kind == ThemeKind.Light)
        {
            Set(0xfdfdfe, 0xf4f5f8, 0xeceef3, 0xdcdee6, 0x24262d, 0x6d7080, 0x6a55e6, 0xf0f1f6, 0xe3e5ee, 0x14151a);
            QuoteText.Color = C(0x555868);
            StatusBack.Color = Panel2.Color;
            PickBack.Color = Sel.Color;
            StatusFg.Color = Mute.Color;
            HelpFg.Color = Accent.Color;
            HelpBack.Color = Color.FromArgb(0x22, 0x6a, 0x55, 0xe6);
            HelpBorder.Color = Accent.Color;
            DiffAddFg.Color = C(0x1d7a38);
            DiffDelFg.Color = C(0xb3261e);
        }
        else if (kind == ThemeKind.Graphite)
        {
            Set(0x1b1c20, 0x202126, 0x17181b, 0x2c2e35, 0xd9dae0, 0x8a8c97, 0xa79bff, 0x26272e, 0x2a2b33, 0xf0f1f5);
            QuoteText.Color = C(0xb4b6bf);
            StatusBack.Color = Panel2.Color;
            PickBack.Color = Sel.Color;
            StatusFg.Color = Mute.Color;
            // «Помощь проекту»: яркая — акцент, заметный фон и обводка
            HelpFg.Color = Accent.Color;
            HelpBack.Color = Color.FromArgb(0x3A, 0xa7, 0x9b, 0xff);
            HelpBorder.Color = Accent.Color;
        }
        else
        {
            Set(0x0d0e0c, 0x10120e, 0x10120e, 0x262a22, 0xcfd3c4, 0x6f7664, 0xe3a63b, 0x1a1d16, 0x1d2018, 0xe6eadb);
            QuoteText.Color = C(0xa9ae9c);
            StatusBack.Color = Accent.Color;
            PickBack.Color = AccentBack.Color;
            StatusFg.Color = Bg.Color;
            // на янтарной полосе — приглушённая тёмная плашка того же цвета, что текст полосы
            HelpFg.Color = Bg.Color;
            HelpBack.Color = Color.FromArgb(0x30, 0, 0, 0);
            HelpBorder.Color = Colors.Transparent;
        }
        Changed?.Invoke();
    }

    static void Set(uint bg, uint panel, uint panel2, uint line, uint fg, uint mute, uint acc, uint code, uint sel, uint heading)
    {
        Bg.Color = C(bg); Panel.Color = C(panel); Panel2.Color = C(panel2); Line.Color = C(line);
        Fg.Color = C(fg); Mute.Color = C(mute); Accent.Color = C(acc); CodeBack.Color = C(code);
        Sel.Color = C(sel); Heading.Color = C(heading); Markup.Color = C(mute); AccentFg.Color = C(panel2);
        AccentBack.Color = Color.FromArgb(0x29, (byte)(acc >> 16), (byte)(acc >> 8), (byte)acc);
        Highlight.Color = Color.FromArgb(0x48, (byte)(acc >> 16), (byte)(acc >> 8), (byte)acc);
        Hover.Color = Color.FromArgb(0x80, (byte)(sel >> 16), (byte)(sel >> 8), (byte)sel);
        Selection.Color = Color.FromArgb(0x55, (byte)(acc >> 16), (byte)(acc >> 8), (byte)acc);
        Found.Color = Color.FromArgb(0x38, (byte)(acc >> 16), (byte)(acc >> 8), (byte)acc);
        FoundNow.Color = Color.FromArgb(0x7A, (byte)(acc >> 16), (byte)(acc >> 8), (byte)acc);
        DiffAdd.Color = Color.FromArgb(0x33, 0x4c, 0xc2, 0x6a);
        DiffDel.Color = Color.FromArgb(0x33, 0xf0, 0x5a, 0x5a);
    }

    /// <summary>Цвета и шрифты текущей темы (с настройками шрифта и размера) для экспорта; <paramref name="width"/> — ширина текста, 0 — по умолчанию.</summary>
    public static Core.ExportStyle ExportStyle(int width)
    {
        string[] fonts = UserFont != null ? [UserFont, Font.Name] : [Font.Name];
        if (!IsTerminal && !fonts.Contains("Segoe UI")) fonts = [.. fonts, "Segoe UI"];
        return new(Bg.Color.ToUInt32(), Panel.Color.ToUInt32(), Fg.Color.ToUInt32(), Mute.Color.ToUInt32(), Accent.Color.ToUInt32(),
            CodeBack.Color.ToUInt32(), Heading.Color.ToUInt32(), QuoteText.Color.ToUInt32(), Highlight.Color.ToUInt32(), Line.Color.ToUInt32(),
            fonts.Distinct().ToArray(), ["Cascadia Mono", "Consolas", "Courier New"], EditorSize, MonoSize, HeadingSize, IsTerminal, width);
    }

    static Color C(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
