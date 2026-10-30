using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Leaflet.UI;

/// <summary>Значки макета (контуры 16×16 из SVG) и мелкие строительные блоки интерфейса.</summary>
static class Ui
{
    public const string File = "M4 1.5h5l3 3V14a.5.5 0 0 1-.5.5h-7.5A.5.5 0 0 1 3.5 14V2A.5.5 0 0 1 4 1.5z M9 1.5v3h3";
    public const string Folder = "M2 4.5a1 1 0 0 1 1-1h3l1.5 1.5H13a1 1 0 0 1 1 1V12a1 1 0 0 1-1 1H3a1 1 0 0 1-1-1z";
    public const string Search = "M2.5 7a4.5 4.5 0 1 0 9 0a4.5 4.5 0 1 0 -9 0 M10.5 10.5L14 14";
    public const string Link = "M6.5 9.5l3-3 M7 4.5l1-1a2.5 2.5 0 0 1 3.5 3.5l-1 1 M9 11.5l-1 1A2.5 2.5 0 0 1 4.5 9l1-1";
    public const string Tag = "M2.5 2.5h5.2l6 6-5.2 5.2-6-6z M4.3 5.3a1 1 0 1 0 2 0a1 1 0 1 0 -2 0";
    public const string Plus = "M8 3v10M3 8h10";
    public const string Chevron = "M6 4l4 4-4 4";
    public const string Close = "M4 4l8 8M12 4l-8 8";
    public const string Minimize = "M3.5 8h9";
    public const string Maximize = "M3.5 3.5h9v9h-9z";
    public const string Question = "M8 14.5a6.5 6.5 0 1 0 0-13a6.5 6.5 0 0 0 0 13z M6 6.2a2 2 0 1 1 2.9 1.8c-.6.3-.9.7-.9 1.4 M8 11.4v.1";
    public const string Heart = "M8 13.5S2 10 2 6a3 3 0 0 1 6-1 3 3 0 0 1 6 1c0 4-6 7.5-6 7.5z";
    public const string Eye = "M1.5 8s2.3-4.5 6.5-4.5S14.5 8 14.5 8 12.2 12.5 8 12.5 1.5 8 1.5 8z M6.2 8a1.8 1.8 0 1 0 3.6 0a1.8 1.8 0 1 0 -3.6 0";
    public const string Command = "M2.5 4.5l3.5 3.5-3.5 3.5 M7.5 12h6";
    public const string Contrast = "M8 2.5a5.5 5.5 0 1 0 0 11z M8 2.5a5.5 5.5 0 1 1 0 11";
    public const string Panel = "M2.5 3.5h11v9h-11z M10 3.5v9";
    public const string OpenFolder = "M2 4.5a1 1 0 0 1 1-1h3l1.5 1.5H13a1 1 0 0 1 1 1V12a1 1 0 0 1-1 1H3a1 1 0 0 1-1-1z M8 7v4 M6 9h4";

    public const string Sliders = "M2 4h1.7 M6.3 4H14 M3.7 4a1.3 1.3 0 1 0 2.6 0a1.3 1.3 0 1 0 -2.6 0 M2 8h7.7 M12.3 8H14 M9.7 8a1.3 1.3 0 1 0 2.6 0a1.3 1.3 0 1 0 -2.6 0 M2 12h3.7 M8.3 12H14 M5.7 12a1.3 1.3 0 1 0 2.6 0a1.3 1.3 0 1 0 -2.6 0";

    static readonly Dictionary<string, Geometry> Geometries = new();

    public static Control Icon(string data, IBrush brush, double size = 15)
    {
        if (!Geometries.TryGetValue(data, out var g)) Geometries[data] = g = Geometry.Parse(data);
        var path = new Path
        {
            Data = g, Stroke = brush, StrokeThickness = 1.3,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        };
        return new Viewbox
        {
            Width = size, Height = size,
            Child = new Canvas { Width = 16, Height = 16, Children = { path } },
        };
    }

    public static void SetIconBrush(Control icon, IBrush brush)
    {
        if (icon is Viewbox { Child: Canvas { Children: [Path p] } }) p.Stroke = brush;
    }

    public static TextBlock Text(string text, double size = 13, IBrush? brush = null, FontWeight weight = FontWeight.Normal) => new()
    {
        Text = text, FontSize = size, Foreground = brush ?? Skin.Fg, FontWeight = weight,
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
    };

    public static TextBlock Caps(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontSize = 11, Foreground = Skin.Mute, FontWeight = FontWeight.Bold,
        LetterSpacing = 0.6, VerticalAlignment = VerticalAlignment.Center,
    };

    public static readonly Cursor Hand = new(StandardCursorType.Hand);

    /// <summary>Кнопка без рамки: подсветка при наведении, действие по щелчку.</summary>
    public static Border Button(Control content, Action click, string? tip = null, double radius = 5, Thickness? padding = null)
    {
        var b = new Border
        {
            Child = content, Background = Brushes.Transparent, Cursor = Hand,
            CornerRadius = new CornerRadius(radius), Padding = padding ?? new Thickness(6),
        };
        b.PointerEntered += (_, _) => b.Background = Skin.Sel;
        b.PointerExited += (_, _) => b.Background = Brushes.Transparent;
        b.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && b.IsPointerOver) { e.Handled = true; click(); }
        };
        if (tip != null) ToolTip.SetTip(b, tip);
        return b;
    }

    public static Border Chip(string text) => new()
    {
        Background = Skin.AccentBack, CornerRadius = new CornerRadius(99), Padding = new Thickness(8, 1),
        Margin = new Thickness(0, 0, 6, 6),
        Child = new TextBlock { Text = text, FontSize = 12, Foreground = Skin.Accent },
    };

    public static Border Kbd(string text) => new()
    {
        BorderBrush = Skin.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
        Padding = new Thickness(5, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = Skin.Mute },
    };

    /// <summary>Текст с выделенным фрагментом (найденное слово, ссылка в обратной ссылке).</summary>
    public static TextBlock Marked(string text, int start, int length, double size, IBrush brush, IBrush mark)
    {
        var tb = new TextBlock { FontSize = size, Foreground = brush, TextWrapping = TextWrapping.Wrap, MaxLines = 3 };
        start = Math.Clamp(start, 0, text.Length);
        length = Math.Clamp(length, 0, text.Length - start);
        if (start > 0) tb.Inlines!.Add(new Avalonia.Controls.Documents.Run(text[..start]));
        if (length > 0) tb.Inlines!.Add(new Avalonia.Controls.Documents.Run(text.Substring(start, length)) { Foreground = mark });
        if (start + length < text.Length) tb.Inlines!.Add(new Avalonia.Controls.Documents.Run(text[(start + length)..]));
        return tb;
    }
}
