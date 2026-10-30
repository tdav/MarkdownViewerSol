using System.Text;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using Leaflet.Core;

namespace Leaflet.Editor;

/// <summary>Текст с оформлением из кусков <see cref="Run"/> (тот же разбор, что в экспорте) — для ячеек таблиц и встроенных заметок.</summary>
static class RichText
{
    public readonly record struct Look(double Size, IBrush Color, bool Bold = false, bool Italic = false, FontFamily? Family = null)
    {
        public Typeface Face => new(Family ?? Skin.EditorFont, Italic ? FontStyle.Italic : FontStyle.Normal, Bold ? FontWeight.Bold : FontWeight.Normal);
        public static Look Body => new(Skin.EditorSize, Skin.Fg);
    }

    /// <summary>Кусок текста раскладки: [Start, End) и исходный кусок (для ссылок под указателем).</summary>
    public readonly record struct Span(int Start, int End, Run Run);

    public static TextLayout Layout(IEnumerable<Run> runs, Look look, double maxWidth = double.PositiveInfinity,
        TextAlignment align = TextAlignment.Left, List<Span>? spans = null)
    {
        var sb = new StringBuilder();
        var overrides = new List<ValueSpan<TextRunProperties>>();
        foreach (var r in runs)
        {
            string t = r.Style.HasFlag(RunStyle.Break) ? "\n"
                : r.Style.HasFlag(RunStyle.Image) ? $"[{(r.Text.Length > 0 ? r.Text : Path.GetFileName(r.Href ?? Loc.T("exp.image")))}]" : r.Text;
            if (t.Length == 0) continue;
            int start = sb.Length;
            sb.Append(t);
            overrides.Add(new ValueSpan<TextRunProperties>(start, t.Length, Props(r.Style, look)));
            spans?.Add(new Span(start, sb.Length, r));
        }
        return new TextLayout(sb.ToString(), look.Face, look.Size, look.Color, align,
            double.IsInfinity(maxWidth) ? TextWrapping.NoWrap : TextWrapping.Wrap, maxWidth: Math.Max(1, maxWidth), textStyleOverrides: overrides);
    }

    public static TextLayout Plain(string text, Look look, double maxWidth = double.PositiveInfinity, TextAlignment align = TextAlignment.Left) =>
        new(text, look.Face, look.Size, look.Color, align, double.IsInfinity(maxWidth) ? TextWrapping.NoWrap : TextWrapping.Wrap, maxWidth: Math.Max(1, maxWidth));

    static TextRunProperties Props(RunStyle s, Look look)
    {
        bool code = s.HasFlag(RunStyle.Code), note = s.HasFlag(RunStyle.Note);
        var face = code ? Skin.Mono : new Typeface(look.Family ?? Skin.EditorFont,
            look.Italic || s.HasFlag(RunStyle.Italic) ? FontStyle.Italic : FontStyle.Normal,
            look.Bold || s.HasFlag(RunStyle.Bold) ? FontWeight.Bold : FontWeight.Normal);
        double size = code ? Skin.MonoSize * look.Size / Skin.EditorSize : note ? look.Size * 0.72 : look.Size;
        IBrush fg = s.HasFlag(RunStyle.Missing) || s.HasFlag(RunStyle.Strike) || s.HasFlag(RunStyle.Image) ? Skin.Markup
            : s.HasFlag(RunStyle.Link) || s.HasFlag(RunStyle.Tag) || note ? Skin.Accent : look.Color;
        IBrush? bg = code ? Skin.CodeBack : s.HasFlag(RunStyle.Mark) ? Skin.Highlight : null;
        var deco = s.HasFlag(RunStyle.Strike) ? TextDecorations.Strikethrough
            : s.HasFlag(RunStyle.Link) && !s.HasFlag(RunStyle.Missing) ? TextDecorations.Underline : null;
        return new GenericTextRunProperties(face, size, deco, fg, bg, note ? BaselineAlignment.Superscript : BaselineAlignment.Baseline);
    }

    /// <summary>Кусок под точкой раскладки или null.</summary>
    public static Run? RunAt(TextLayout layout, List<Span> spans, Avalonia.Point p)
    {
        var hit = layout.HitTestPoint(p);
        if (!hit.IsInside) return null;
        int i = hit.TextPosition;
        foreach (var s in spans) if (i >= s.Start && i < s.End) return s.Run;
        return null;
    }
}
