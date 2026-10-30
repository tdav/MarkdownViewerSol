using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Leaflet.Core;

namespace Leaflet.Editor;

/// <summary>
/// Встроенная заметка ![[Имя]] / ![[Имя#раздел]] под строкой: блоки из <see cref="Export.Parse"/> свёрстаны по ширине
/// и нарисованы (только показ, править можно в самой заметке). Выше <see cref="MaxHeight"/> — обрезается с затуханием.
/// Вложенные встраивания — рамкой внутри (круги и глубину ограничивает <see cref="Export.Embedder"/>).
/// </summary>
public sealed class NoteView
{
    public const double MaxHeight = 520, Pad = 12;

    readonly List<Action<DrawingContext, Point>> _ops;
    public string Title { get; }
    public string? Message { get; }
    public double Width { get; }
    public double Height { get; }
    public double FullHeight { get; }
    /// <summary>Строка с именем заметки (от верха вида): щелчок открывает заметку.</summary>
    public double HeaderHeight { get; }
    public bool Clipped => FullHeight > Height;

    public NoteView(Block embed, double width, ImageCache cache, double scaling, string? pageDir)
    {
        Title = embed.Title;
        Message = embed.Message.Length > 0 ? embed.Message : null;
        Width = width;
        var w = new Writer(cache, scaling, pageDir);
        var title = RichText.Plain("↳ " + embed.Title, new RichText.Look(Skin.EditorSize * 0.85, Skin.Mute));
        HeaderHeight = Pad * 0.6 + title.Height + Pad * 0.4;
        w.Y = HeaderHeight;
        if (Message != null) w.Text([new Run(Message, RunStyle.Italic)], new RichText.Look(Skin.EditorSize, Skin.Mute), Pad, width - 2 * Pad);
        else w.Blocks(embed.Children, Pad, width - 2 * Pad, embed.BaseDir);
        FullHeight = w.Y + Pad;
        Height = Math.Min(FullHeight, MaxHeight);
        _ops = w.Ops;
        double top = Pad * 0.6;
        _ops.Insert(0, (dc, o) => title.Draw(dc, new Point(o.X + Pad, o.Y + top)));
    }

    public void Draw(DrawingContext dc, Point origin)
    {
        var rect = new Rect(origin, new Size(Width, Height));
        dc.DrawRectangle(Skin.Panel, new Pen(Skin.Line, 1), rect.Deflate(0.5), 6, 6);
        using (dc.PushClip(rect.Deflate(1)))
        {
            foreach (var op in _ops) op(dc, origin);
            if (Clipped)
            {
                var fade = new Rect(rect.X, rect.Bottom - 48, rect.Width, 48);
                var c = Skin.Panel.Color;
                dc.FillRectangle(new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0), new GradientStop(c, 0.85) },
                }, fade);
            }
        }
    }

    /// <summary>Вёрстка блоков сверху вниз: каждая часть — отложенное рисование со своим местом.</summary>
    sealed class Writer(ImageCache cache, double scaling, string? pageDir)
    {
        public readonly List<Action<DrawingContext, Point>> Ops = [];
        public double Y;
        static double Size => Skin.EditorSize;

        public void Text(IEnumerable<Run> runs, RichText.Look look, double x, double w)
        {
            var layout = RichText.Layout(runs, look, Math.Max(10, w));
            double y = Y;
            Ops.Add((dc, o) => layout.Draw(dc, new Point(o.X + x, o.Y + y)));
            Y += layout.Height;
        }

        void Fill(IBrush brush, Rect r) => Ops.Add((dc, o) => dc.FillRectangle(brush, r.Translate(new Vector(o.X, o.Y))));

        public void Blocks(List<Block> blocks, double x, double w, string? baseDir)
        {
            BlockKind? prev = null;
            foreach (var b in blocks)
            {
                if (prev != null) Y += prev == BlockKind.Item && b.Kind == BlockKind.Item ? Size * 0.2 : Size * 0.6;
                switch (b.Kind)
                {
                    case BlockKind.Heading:
                        Text(b.Runs, new RichText.Look(Skin.HeadingSize[b.Level], Skin.Heading, Bold: true), x, w);
                        break;
                    case BlockKind.Paragraph when b.Runs.Count > 0 && b.Runs.All(r => r.Style == RunStyle.Image):
                        Images(b.Runs, x, w, baseDir);
                        break;
                    case BlockKind.Paragraph:
                        Text(b.Runs, RichText.Look.Body, x, w);
                        break;
                    case BlockKind.Item:
                        Item(b, x, w);
                        break;
                    case BlockKind.Quote:
                    {
                        double y0 = Y;
                        Text(b.Runs, new RichText.Look(Size, Skin.QuoteText, Italic: true), x + 14, w - 14);
                        Fill(Skin.Accent, new Rect(x, y0 + 2, 3, Y - y0 - 4));
                        break;
                    }
                    case BlockKind.Code:
                    {
                        var layout = RichText.Plain(b.Code, new RichText.Look(Skin.MonoSize, Skin.Fg, Family: Skin.Mono.FontFamily), Math.Max(10, w - 16));
                        Fill(Skin.CodeBack, new Rect(x, Y, w, layout.Height + 16));
                        double y = Y + 8;
                        Ops.Add((dc, o) => layout.Draw(dc, new Point(o.X + x + 8, o.Y + y)));
                        Y += layout.Height + 16;
                        break;
                    }
                    case BlockKind.Rule:
                        Fill(Skin.Line, new Rect(x, Y + Size * 0.5, w, 1));
                        Y += Size;
                        break;
                    case BlockKind.Table:
                        Table(b, x, w);
                        break;
                    case BlockKind.Embed:
                    {
                        double y0 = Y;
                        Text([new Run("↳ " + b.Title, RunStyle.None)], new RichText.Look(Size * 0.85, Skin.Mute), x + 14, w - 14);
                        Y += Size * 0.2;
                        if (b.Message.Length > 0) Text([new Run(b.Message, RunStyle.Italic)], new RichText.Look(Size, Skin.Mute), x + 14, w - 14);
                        else Blocks(b.Children, x + 14, w - 14, b.BaseDir);
                        Fill(Skin.Line, new Rect(x, y0, 3, Y - y0));
                        break;
                    }
                    case BlockKind.Footnotes:
                    {
                        Fill(Skin.Line, new Rect(x, Y, w / 3, 1));
                        Y += Size * 0.5;
                        var small = new RichText.Look(Size * 0.88, Skin.Fg);
                        foreach (var n in b.Children)
                        {
                            Marker(n.Marker + ".", Skin.Mute, x, small.Size, false);
                            Text(n.Runs, small, x + Size * 1.6, w - Size * 1.6);
                        }
                        break;
                    }
                }
                prev = b.Kind;
            }
        }

        /// <summary>Маркер в начале строки текста, который будет нарисован следующим (по базовой линии того же размера).</summary>
        double Marker(string marker, IBrush brush, double x, double size, bool symbols)
        {
            var text = new FormattedText(marker, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                symbols ? Skin.Symbols : new Typeface(Skin.EditorFont), size, brush);
            var line = RichText.Plain("x", new RichText.Look(size, brush));
            double y = Y + line.TextLines[0].Baseline - text.Baseline;
            Ops.Add((dc, o) => dc.DrawText(text, new Point(o.X + x, o.Y + y)));
            return text.WidthIncludingTrailingWhitespace;
        }

        void Item(Block b, double x, double w)
        {
            double ix = x + b.Level * ListMarkerElement.Step;
            string marker = b.Marker == "•" ? new[] { "•", "◦", "▪" }[b.Level % 3] : b.Marker;
            double mw = Math.Max(Size * (b.Ordered ? 1.6 : 1.2), Marker(marker, b.Marker == "☑" ? Skin.Accent : Skin.Mute, ix, Size, !b.Ordered) + Size * 0.35);
            var look = b.Marker == "☑" ? new RichText.Look(Size, Skin.Markup) : RichText.Look.Body;
            Text(b.Runs, look, ix + mw, w - (ix - x) - mw);
        }

        void Images(List<Run> runs, double x, double w, string? baseDir)
        {
            foreach (var r in runs)
            {
                string? path = Export.ImagePath(r.Href!, baseDir) ?? Export.ImagePath(r.Href!, pageDir);
                if (path != null && Vault.IsImage(path) && cache.Size(path) is { } px)
                {
                    double iw = px.Width / scaling, ih = px.Height / scaling, k = Math.Min(1, Math.Min(w / iw, 300 / ih));
                    var rect = new Rect(x, Y, Math.Max(1, Math.Round(iw * k)), Math.Max(1, Math.Round(ih * k)));
                    int decode = Math.Max(1, (int)Math.Ceiling(rect.Width * scaling));
                    Ops.Add((dc, o) =>
                    {
                        var at = rect.Translate(new Vector(o.X, o.Y));
                        if (cache.Get(path, decode) is { } bmp) dc.DrawImage(bmp, new Rect(bmp.Size), at);
                        else dc.FillRectangle(Skin.CodeBack, at);
                    });
                    Y += rect.Height + 4;
                }
                else Text([new Run(r.Text.Length > 0 ? r.Text : Path.GetFileName(r.Href ?? ""), RunStyle.Image, r.Href)], RichText.Look.Body, x, w);
            }
        }

        void Table(Block b, double x, double w)
        {
            int cols = b.Rows.Max(r => r.Count);
            const double padX = 8, padY = 3;
            var need = new List<double>(Enumerable.Repeat(padX * 3, cols));
            for (int r = 0; r < b.Rows.Count; r++)
                for (int c = 0; c < b.Rows[r].Count; c++)
                    need[c] = Math.Max(need[c], RichText.Layout(b.Rows[r][c], TablePreview.CellLook(r == 0)).WidthIncludingTrailingWhitespace + 2 * padX);
            if (need.Sum() > w) TablePreview.Squeeze(need, w, padX * 4);
            var xs = new double[cols + 1];
            for (int c = 0; c < cols; c++) xs[c + 1] = xs[c] + need[c];
            for (int r = 0; r < b.Rows.Count; r++)
            {
                var cells = new TextLayout?[cols];
                double h = 0;
                for (int c = 0; c < b.Rows[r].Count; c++)
                {
                    char a = c < b.Align.Length ? b.Align[c] : 'l';
                    cells[c] = RichText.Layout(b.Rows[r][c], TablePreview.CellLook(r == 0), Math.Max(4, need[c] - 2 * padX),
                        a == 'c' ? TextAlignment.Center : a == 'r' ? TextAlignment.Right : TextAlignment.Left);
                    h = Math.Max(h, cells[c]!.Height);
                }
                h += 2 * padY;
                double y = Y;
                if (r == 0) Fill(Skin.CodeBack, new Rect(x, y, xs[cols], h));
                Fill(Skin.Line, new Rect(x, y, xs[cols] + 1, 1));
                for (int c = 0; c <= cols; c++) Fill(Skin.Line, new Rect(x + Math.Round(xs[c]), y, 1, h));
                Ops.Add((dc, o) =>
                {
                    for (int c = 0; c < cols; c++) cells[c]?.Draw(dc, new Point(o.X + x + xs[c] + padX, o.Y + y + padY));
                });
                Y += h;
            }
            Fill(Skin.Line, new Rect(x, Y, xs[cols] + 1, 1));
            Y += 1;
        }
    }
}
