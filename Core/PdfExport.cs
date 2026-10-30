using System.Text;
using SkiaSharp;

namespace Leaflet.Core;

/// <summary>
/// PDF без WebView: Skia уже есть в Avalonia и умеет писать PDF (шрифты встраиваются подмножеством, текст выделяется и ищется).
/// Вёрстка своя: блоки из <see cref="Export.Parse"/>, перенос по словам, страницы A4 в цветах темы.
/// [[Ссылки]] на разделы — переходы внутри файла, на другие заметки — ссылки на соседние .pdf.
/// </summary>
public static class PdfExport
{
    public static void Write(Stream stream, string title, List<Block> blocks, ExportStyle st, string? baseDir = null)
    {
        using var w = new Writer(stream, title, st, baseDir, Chars(blocks));
        w.Blocks(blocks);
    }

    /// <summary>Все знаки документа (и служебные: номера страниц, «#», «[изображение]») — по ним урезаются шрифты.</summary>
    static string Chars(List<Block> blocks)
    {
        var set = new HashSet<int>();
        void Add(string s) { foreach (var r in s.EnumerateRunes()) set.Add(r.Value); }
        Add("0123456789#[] " + Loc.T("exp.image") + ".↩");
        void Walk(List<Block> list)
        {
            foreach (var b in list)
            {
                foreach (var r in b.Runs.Concat(b.Rows.SelectMany(row => row.SelectMany(c => c)))) Add(r.Text);
                Add(b.Marker);
                Add(b.Code);
                Add(b.Title);
                Add(b.Message);
                Walk(b.Children);
            }
        }
        Walk(blocks);
        var sb = new StringBuilder();
        foreach (int cp in set) if (!char.IsControl((char)Math.Min(cp, 0xFFFF)) || cp > 0xFFFF) sb.Append(char.ConvertFromUtf32(cp));
        return sb.ToString();
    }

    sealed class Piece
    {
        public string Text = "";
        public SKFont Font = null!;
        public SKColor Color;
        public SKColor? Back;
        public bool Under, Strike;
        public string? Href, Anchor;   // Anchor — именованное место (первая ссылка на сноску)
        public float W, X, Raise;      // Raise — подъём над строкой (номер сноски)
    }

    sealed class Line
    {
        public readonly List<Piece> Pieces = [];
        public float W, H, Ascent, Descent;
    }

    readonly record struct Look(float Size, SKColor Color, bool Bold = false, bool Italic = false, bool Mono = false, float Leading = 1.6f);

    sealed class Writer : IDisposable
    {
        const float PageW = 595.28f, PageH = 841.89f, Margin = 56, Px = 0.75f; // A4 в пунктах; 1 px = 0,75 pt

        readonly SKDocument _doc;
        readonly ExportStyle _st;
        string? _baseDir;
        readonly string? _pageDir;
        readonly string _body, _mono, _chars;
        readonly string? _symbols;
        readonly Dictionary<(string, bool, bool), SKTypeface> _faces = [];
        readonly Dictionary<(int, bool, bool), SKTypeface?> _fallback = [];
        readonly Dictionary<(SKTypeface, float, bool, bool), SKFont> _fonts = [];
        readonly SKPaint _paint = new() { IsAntialias = true };
        SKCanvas? _canvas;
        float _y, _x = Margin, _w = PageW - 2 * Margin;
        /// <summary>Полосы слева у встроенных заметок, которые ещё не закончились: дорисовываются на каждой странице.</summary>
        readonly List<Bar> _bars = [];
        sealed class Bar { public float X, Top; }
        int _page;

        float Body => (float)_st.Size * Px;
        float Width => _w;
        float Left => _x;
        static SKColor C(uint argb) => new(argb);

        public Writer(Stream stream, string title, ExportStyle st, string? baseDir, string chars)
        {
            _st = st;
            _chars = chars;
            _baseDir = _pageDir = baseDir;
            _body = Installed(st.Fonts) ?? "Segoe UI";
            _mono = Installed(st.MonoFonts) ?? "Consolas";
            _symbols = Installed(["Segoe UI Symbol"]);
            var now = DateTime.Now;
            _doc = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata
            {
                Title = title, Creator = "Leaflet", Producer = "Leaflet", Creation = now, Modified = now, RasterDpi = 150,
            });
            NewPage();
        }

        static string? Installed(string[] names)
        {
            var families = SKFontManager.Default.FontFamilies.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return names.FirstOrDefault(families.Contains);
        }

        // ---------- страницы ----------

        void NewPage()
        {
            EndPage();
            _canvas = _doc.BeginPage(PageW, PageH);
            _canvas.DrawColor(C(_st.Bg));
            _page++;
            _y = Margin;
            foreach (var bar in _bars) bar.Top = Margin;
        }

        void EndPage()
        {
            if (_canvas == null) return;
            foreach (var bar in _bars) DrawBar(bar);
            var font = Font(false, false, false, Body * 0.75f);
            _paint.Color = C(_st.Mute);
            _canvas.DrawText(_page.ToString(), PageW / 2, PageH - Margin / 2, SKTextAlign.Center, font, _paint);
            _doc.EndPage();
            _canvas = null;
        }

        void Ensure(float h)
        {
            if (_y + h > PageH - Margin && _y > Margin + 0.5f) NewPage();
        }

        void Space(float gap) { if (_y > Margin + 0.5f) _y += gap; }

        void DrawBar(Bar bar)
        {
            if (_y <= bar.Top) return;
            _paint.Color = C(_st.Line);
            _canvas!.DrawRect(new SKRect(bar.X, bar.Top, bar.X + 3 * Px, _y), _paint);
        }

        public void Dispose()
        {
            EndPage();
            _doc.Close();
            _doc.Dispose();
            foreach (var f in _fonts.Values) f.Dispose();
            foreach (var t in _faces.Values.Concat(_fallback.Values.OfType<SKTypeface>()).Distinct()) t.Dispose();
            _paint.Dispose();
        }

        // ---------- шрифты ----------

        SKTypeface Face(string family, bool bold, bool italic)
        {
            if (_faces.TryGetValue((family, bold, italic), out var face)) return face;
            face = SKTypeface.FromFamilyName(family, bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                SKFontStyleWidth.Normal, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright) ?? SKTypeface.FromFamilyName("Segoe UI");
            return _faces[(family, bold, italic)] = Subset(face);
        }

        /// <summary>Шрифт только со знаками документа (см. <see cref="FontSubset"/>); если не вышло — исходный.</summary>
        SKTypeface Subset(SKTypeface face)
        {
            try
            {
                using var stream = face.OpenStream(out int index);
                if (stream == null || stream.Length <= 0) return face;
                var bytes = new byte[stream.Length];
                if (stream.Read(bytes, bytes.Length) != bytes.Length) return face;
                var map = new List<(int, ushort)>();
                foreach (var r in _chars.EnumerateRunes())
                    if (face.GetGlyph(r.Value) is var g and > 0) map.Add((r.Value, g));
                if (FontSubset.Make(bytes, index, map) is not { } data) return face;
                var sub = SKTypeface.FromData(SKData.CreateCopy(data));
                if (sub == null) return face;
                face.Dispose();
                return sub;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException) { return face; }
        }

        SKFont Font(SKTypeface face, float size, bool bold, bool italic)
        {
            if (_fonts.TryGetValue((face, size, bold, italic), out var font)) return font;
            // У Consolas нет полужирного: утолщаем сами, как это делает Windows.
            font = new SKFont(face, size)
            {
                Subpixel = true,
                Embolden = bold && face.FontWeight < 600,
                SkewX = italic && face.FontSlant == SKFontStyleSlant.Upright ? -0.2f : 0,
            };
            return _fonts[(face, size, bold, italic)] = font;
        }

        SKFont Font(bool mono, bool bold, bool italic, float size) => Font(Face(mono ? _mono : _body, bold, italic), size, bold, italic);

        /// <summary>Шрифт для знака, которого нет в основном (☐, ☑, стрелки, эмодзи): подбирает Windows.</summary>
        SKTypeface? Fallback(int cp, SKTypeface primary, bool bold, bool italic)
        {
            if (_fallback.TryGetValue((cp, bold, italic), out var face)) return face;
            // Один урезанный шрифт на семейство: иначе каждый знак встроил бы свою копию.
            using var match = SKFontManager.Default.MatchCharacter(primary.FamilyName, primary.FontStyle, null, cp);
            face = match == null ? null : Face(match.FamilyName, bold, italic);
            return _fallback[(cp, bold, italic)] = face;
        }

        // ---------- вёрстка строки ----------

        /// <summary>Режет куски на строки шириной <paramref name="width"/>; перенос по пробелам, слишком длинное слово — по буквам.</summary>
        List<Line> Flow(IEnumerable<Run> runs, float width, Look look, string? prefix = null)
        {
            var lines = new List<Line>();
            var cur = new Line();
            var word = new List<Piece>();
            Piece? space = null;
            bool wrapped = false;

            void Finish(bool wrap)
            {
                Measure(cur, look);
                lines.Add(cur);
                cur = new Line();
                space = null;
                wrapped = wrap;
            }
            void Add(Piece p) { p.X = cur.W; cur.W += p.W; cur.Pieces.Add(p); }
            void PlaceWord()
            {
                if (word.Count == 0) return;
                float wordW = word.Sum(p => p.W);
                if (cur.Pieces.Count > 0 && cur.W + (space?.W ?? 0) + wordW > width) Finish(true);
                else if (space != null && cur.Pieces.Count > 0) Add(space);
                space = null;
                foreach (var p in word)
                {
                    var rest = p;
                    while (cur.W + rest.W > width && rest.Text.Length > 1)
                    {
                        int fit = Fit(rest, width - cur.W);
                        if (fit == 0) { if (cur.Pieces.Count > 0) { Finish(true); continue; } fit = 1; }
                        Add(Clone(rest, rest.Text[..fit]));
                        rest = Clone(rest, rest.Text[fit..]);
                        Finish(true);
                    }
                    Add(rest);
                }
                word.Clear();
            }

            if (prefix != null) foreach (var p in Pieces(prefix, look, look with { Bold = false, Color = C(_st.Mute) }, RunStyle.None, null)) Add(p);
            foreach (var run in runs)
            {
                if (run.Style.HasFlag(RunStyle.Break)) { PlaceWord(); Finish(false); continue; }
                string text = run.Style.HasFlag(RunStyle.Image) ? $"[{(run.Text.Length > 0 ? run.Text : Loc.T("exp.image"))}]" : run.Text;
                int i = 0;
                while (i < text.Length)
                {
                    bool ws = char.IsWhiteSpace(text[i]);
                    int j = i;
                    while (j < text.Length && char.IsWhiteSpace(text[j]) == ws) j++;
                    string part = text[i..j];
                    var pieces = Pieces(part, look, look, run.Style, run.Href, i == 0 ? run.Id : null);
                    if (!ws) word.AddRange(pieces);
                    else
                    {
                        PlaceWord();
                        var sp = Join(pieces);
                        if (cur.Pieces.Count == 0 && !wrapped) Add(sp); // отступ в начале строки (код) сохраняем
                        else if (cur.Pieces.Count > 0) space = space == null ? sp : Clone(space, space.Text + sp.Text);
                    }
                    i = j;
                }
            }
            PlaceWord();
            if (cur.Pieces.Count > 0 || lines.Count == 0 || !wrapped) Finish(false);
            return lines;
        }

        static Piece Join(List<Piece> pieces) => pieces.Count == 1 ? pieces[0] : Clone(pieces[0], string.Concat(pieces.Select(p => p.Text)));

        static Piece Clone(Piece p, string text) => new()
        {
            Text = text, Font = p.Font, Color = p.Color, Back = p.Back, Under = p.Under, Strike = p.Strike, Href = p.Href, Raise = p.Raise,
            W = p.Font.MeasureText(text),
        };

        static int Fit(Piece p, float avail)
        {
            int lo = 0, hi = p.Text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (p.Font.MeasureText(p.Text.AsSpan(0, mid)) <= avail) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>Куски одного оформления; знаки, которых нет в шрифте, уходят в запасной шрифт.</summary>
        List<Piece> Pieces(string text, Look block, Look look, RunStyle s, string? href, string? anchor = null)
        {
            bool code = s.HasFlag(RunStyle.Code), mono = look.Mono || code, note = s.HasFlag(RunStyle.Note);
            bool bold = look.Bold || s.HasFlag(RunStyle.Bold), italic = look.Italic || s.HasFlag(RunStyle.Italic);
            float size = code && !look.Mono ? block.Size * (float)(_st.MonoSize / _st.Size) : note ? look.Size * 0.7f : look.Size;
            var color = s.HasFlag(RunStyle.Missing) || s.HasFlag(RunStyle.Strike) || s.HasFlag(RunStyle.Image) ? C(_st.Mute)
                : s.HasFlag(RunStyle.Link) || s.HasFlag(RunStyle.Tag) || note ? C(_st.Accent) : look.Color;
            SKColor? back = code && !look.Mono ? C(_st.CodeBack) : s.HasFlag(RunStyle.Mark) ? C(_st.Highlight) : null;
            bool under = s.HasFlag(RunStyle.Link), strike = s.HasFlag(RunStyle.Strike);
            if (s.HasFlag(RunStyle.Missing) || s.HasFlag(RunStyle.Image)) href = null;

            var primary = Face(mono ? _mono : _body, bold, italic);
            var result = new List<Piece>();
            var sb = new StringBuilder();
            SKTypeface? curFace = null;
            void Flush()
            {
                if (sb.Length == 0) return;
                var font = Font(curFace!, size, bold, italic);
                string t = sb.ToString();
                result.Add(new Piece { Text = t, Font = font, Color = color, Back = back, Under = under, Strike = strike, Href = href, W = font.MeasureText(t) });
                sb.Clear();
            }
            foreach (var rune in text.EnumerateRunes())
            {
                var face = rune.Value < 0x80 || primary.ContainsGlyph(rune.Value) ? primary : Fallback(rune.Value, primary, bold, italic) ?? primary;
                if (face != curFace) { Flush(); curFace = face; }
                sb.Append(rune.ToString());
            }
            Flush();
            if (note) foreach (var p in result) p.Raise = look.Size * 0.33f;
            if (result.Count > 0) result[0].Anchor = anchor;
            return result;
        }

        void Measure(Line line, Look look)
        {
            float a = 0, d = 0, size = look.Size;
            foreach (var p in line.Pieces)
            {
                var m = p.Font.Metrics;
                a = Math.Max(a, -m.Ascent);
                d = Math.Max(d, m.Descent);
                size = Math.Max(size, p.Font.Size);
            }
            if (line.Pieces.Count == 0)
            {
                var m = Font(look.Mono, look.Bold, look.Italic, look.Size).Metrics;
                a = -m.Ascent; d = m.Descent;
            }
            line.Ascent = a;
            line.Descent = d;
            line.H = Math.Max(size * look.Leading, a + d);
        }

        // ---------- рисование ----------

        float Baseline(Line l, float top) => top + (l.H - l.Ascent - l.Descent) / 2 + l.Ascent;

        void DrawLine(Line l, float x, float top)
        {
            var c = _canvas!;
            float baseline = Baseline(l, top);
            foreach (var p in l.Pieces)
            {
                float px = x + p.X;
                var m = p.Font.Metrics;
                if (p.Back is { } back)
                {
                    _paint.Color = back;
                    c.DrawRect(new SKRect(px, baseline + m.Ascent - 1, px + p.W, baseline + m.Descent + 1), _paint);
                }
                _paint.Color = p.Color;
                c.DrawText(p.Text, px, baseline - p.Raise, SKTextAlign.Left, p.Font, _paint);
                if (p.Anchor != null) c.DrawNamedDestinationAnnotation(new SKPoint(px, top), p.Anchor);
                float t = Math.Max(0.5f, p.Font.Size * 0.06f);
                if (p.Under) c.DrawRect(new SKRect(px, baseline + p.Font.Size * 0.12f, px + p.W, baseline + p.Font.Size * 0.12f + t), _paint);
                if (p.Strike) c.DrawRect(new SKRect(px, baseline - p.Font.Size * 0.3f, px + p.W, baseline - p.Font.Size * 0.3f + t), _paint);
                if (p.Href is { } href && p.Text.Trim().Length > 0)
                {
                    var rect = new SKRect(px, top, px + p.W, top + l.H);
                    if (href.StartsWith('#')) { if (href.Length > 1) c.DrawLinkDestinationAnnotation(rect, href[1..]); }
                    else c.DrawUrlAnnotation(rect, href);
                }
            }
        }

        /// <summary>Строки сверху вниз с переходом на новую страницу; <paramref name="deco"/> рисует под строкой (полоса цитаты, фон кода, маркер).</summary>
        void Lines(List<Line> lines, float x, Action<int, Line, float>? deco = null)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                Ensure(l.H);
                deco?.Invoke(i, l, _y);
                DrawLine(l, x, _y);
                _y += l.H;
            }
        }

        // ---------- блоки ----------

        /// <param name="nested">Блоки встроенной заметки: у заголовков нет именованных мест (они есть у страницы).</param>
        public void Blocks(List<Block> blocks, bool nested = false)
        {
            Look text = new(Body, C(_st.Fg));
            BlockKind? prev = null;
            foreach (var b in blocks)
            {
                float gap = prev == null ? 0 : prev == BlockKind.Item && b.Kind == BlockKind.Item ? Body * 0.25f : Body * 0.85f;
                switch (b.Kind)
                {
                    case BlockKind.Heading:
                    {
                        float size = (float)_st.HeadingSizes[b.Level] * Px;
                        var look = new Look(size, C(_st.Heading), Bold: true, Leading: 1.3f);
                        var lines = Flow(b.Runs, Width, look, _st.HeadingMarks ? new string('#', b.Level) + " " : null);
                        Space(Math.Max(gap, size * 1.0f));
                        Ensure(lines[0].H + Body * 1.6f * 2); // заголовок не остаётся один внизу страницы
                        if (b.Id.Length > 0 && !nested) _canvas!.DrawNamedDestinationAnnotation(new SKPoint(Left, _y), b.Id);
                        Lines(lines, Left);
                        _y += size * 0.2f;
                        break;
                    }
                    case BlockKind.Paragraph:
                        Space(gap);
                        if (b.Runs.Count > 0 && b.Runs.All(r => r.Style == RunStyle.Image))
                        {
                            for (int k = 0; k < b.Runs.Count; k++)
                            {
                                if (k > 0) Space(gap);
                                if (!DrawImage(b.Runs[k])) Lines(Flow([b.Runs[k]], Width, text), Left);
                            }
                            break;
                        }
                        Lines(Flow(b.Runs, Width, text), Left);
                        break;
                    case BlockKind.Item:
                    {
                        Space(gap);
                        var markLook = text with { Color = C(b.Marker == "☑" ? _st.Accent : _st.Mute) };
                        Line marker;
                        if (b.Marker is "☐" or "☑" && _symbols != null)
                        {
                            // Как в редакторе: «☐☑» шрифтом символов, иначе Windows подставит широкий шрифт эмодзи.
                            marker = new Line();
                            var font = Font(Face(_symbols, false, false), Body, false, false);
                            marker.Pieces.Add(new Piece { Text = b.Marker, Font = font, Color = markLook.Color, W = font.MeasureText(b.Marker) });
                        }
                        else marker = Flow([new Run(b.Marker, RunStyle.None)], float.MaxValue, markLook)[0];
                        float indent = Left + b.Level * Body * 1.6f;
                        float mark = Math.Max(Body * (b.Ordered ? 1.9f : 1.4f), marker.Pieces.Sum(p => p.W) + Body * 0.45f);
                        var lines = Flow(b.Runs, Width - (indent - Left) - mark, text);
                        Lines(lines, indent + mark, (i, l, top) =>
                        {
                            if (i != 0) return;
                            var tmp = new Line { Ascent = l.Ascent, Descent = l.Descent, H = l.H };
                            tmp.Pieces.AddRange(marker.Pieces);
                            DrawLine(tmp, indent, top);
                        });
                        break;
                    }
                    case BlockKind.Quote:
                    {
                        Space(gap);
                        float bar = 3 * Px, pad = 14 * Px;
                        var look = text with { Italic = true, Color = C(_st.QuoteText) };
                        Lines(Flow(b.Runs, Width - pad, look), Left + pad, (_, l, top) =>
                        {
                            _paint.Color = C(_st.Accent);
                            _canvas!.DrawRect(new SKRect(Left, top, Left + bar, top + l.H), _paint);
                        });
                        break;
                    }
                    case BlockKind.Code:
                    {
                        Space(gap);
                        float pad = 12 * Px, size = (float)_st.MonoSize * Px;
                        var runs = new List<Run>();
                        foreach (var line in b.Code.Split('\n'))
                        {
                            if (runs.Count > 0) runs.Add(new Run("\n", RunStyle.Break));
                            if (line.Length > 0) runs.Add(new Run(line.Replace("\t", "    "), RunStyle.None));
                        }
                        var lines = Flow(runs, Width - 2 * pad, new Look(size, C(_st.Fg), Mono: true, Leading: 1.45f));
                        _paint.Color = C(_st.CodeBack);
                        Ensure(lines[0].H + pad);
                        _canvas!.DrawRect(new SKRect(Left, _y, Left + Width, _y + pad), _paint);
                        _y += pad;
                        Lines(lines, Left + pad, (_, l, top) =>
                        {
                            _paint.Color = C(_st.CodeBack);
                            _canvas!.DrawRect(new SKRect(Left, top, Left + Width, top + l.H), _paint);
                        });
                        _paint.Color = C(_st.CodeBack);
                        _canvas!.DrawRect(new SKRect(Left, _y, Left + Width, _y + pad), _paint);
                        _y += pad;
                        break;
                    }
                    case BlockKind.Rule:
                        Space(gap + Body * 0.5f);
                        Ensure(1);
                        _paint.Color = C(_st.Line);
                        _canvas!.DrawRect(new SKRect(Left, _y, Left + Width, _y + 1), _paint);
                        _y += 1 + Body * 0.5f;
                        break;
                    case BlockKind.Table:
                        Space(gap);
                        Table(b, text);
                        break;
                    case BlockKind.Embed:
                        Space(gap);
                        Embed(b, text);
                        break;
                    case BlockKind.Footnotes:
                        Space(gap + Body * 0.5f);
                        Footnotes(b, text);
                        break;
                }
                prev = b.Kind;
            }
        }

        void Table(Block b, Look text)
        {
            int cols = b.Rows.Max(r => r.Count);
            float pad = 6 * Px, padX = 10 * Px;
            var head = new Look(Body, C(_st.Heading), Bold: true, Leading: 1.45f);
            var cell = text with { Leading = 1.45f };
            // Ширина столбцов по самому длинному содержимому, ужимается пропорционально, если не влезает.
            var natural = new float[cols];
            for (int r = 0; r < b.Rows.Count; r++)
                for (int c = 0; c < b.Rows[r].Count; c++)
                {
                    float w = Flow(b.Rows[r][c], float.MaxValue, r == 0 ? head : cell).Max(l => l.W);
                    natural[c] = Math.Max(natural[c], Math.Max(w + 2 * padX, 30));
                }
            float total = natural.Sum(), k = total > Width ? Width / total : 1;
            var widths = natural.Select(w => w * k).ToArray();

            for (int r = 0; r < b.Rows.Count; r++)
            {
                var cells = Enumerable.Range(0, cols)
                    .Select(c => c < b.Rows[r].Count ? Flow(b.Rows[r][c], widths[c] - 2 * padX, r == 0 ? head : cell) : [])
                    .ToList();
                float h = cells.Max(ls => ls.Sum(l => l.H)) + 2 * pad;
                Ensure(h);
                float x = Left;
                for (int c = 0; c < cols; c++)
                {
                    var rect = new SKRect(x, _y, x + widths[c], _y + h);
                    if (r == 0) { _paint.Color = C(_st.Panel); _canvas!.DrawRect(rect, _paint); }
                    float y = _y + pad;
                    char align = c < b.Align.Length ? b.Align[c] : 'l';
                    foreach (var l in cells[c])
                    {
                        float free = widths[c] - 2 * padX - l.W;
                        DrawLine(l, x + padX + (align == 'c' ? free / 2 : align == 'r' ? free : 0), y);
                        y += l.H;
                    }
                    _paint.Color = C(_st.Line);
                    _paint.Style = SKPaintStyle.Stroke;
                    _paint.StrokeWidth = Px;
                    _canvas!.DrawRect(rect, _paint);
                    _paint.Style = SKPaintStyle.Fill;
                    x += widths[c];
                }
                _y += h;
            }
        }

        /// <summary>Встроенная заметка: полоса слева, имя заметки (ссылкой на её файл), её блоки с отступом.</summary>
        void Embed(Block b, Look text)
        {
            float pad = 14 * Px;
            var title = Flow([new Run(b.Title, RunStyle.None, b.Href)], Width - pad, text with { Size = Body * 0.85f, Color = C(_st.Mute) });
            Ensure(title[0].H + Body * 1.6f);
            var (x, w, dir) = (_x, _w, _baseDir);
            var bar = new Bar { X = x, Top = _y };
            _bars.Add(bar);
            _x += pad;
            _w -= pad;
            _baseDir = b.BaseDir ?? _baseDir;
            Lines(title, Left);
            _y += Body * 0.3f;
            if (b.Message.Length > 0) Lines(Flow([new Run(b.Message, RunStyle.None)], Width, text with { Italic = true, Color = C(_st.Mute) }), Left);
            else Blocks(b.Children, nested: true);
            _bars.Remove(bar);
            DrawBar(bar);
            (_x, _w, _baseDir) = (x, w, dir);
        }

        /// <summary>Сноски в конце: короткая черта, номера, текст мельче; переходы к ссылке и обратно.</summary>
        void Footnotes(Block b, Look text)
        {
            Ensure(Body * 2.5f);
            _paint.Color = C(_st.Line);
            _canvas!.DrawRect(new SKRect(Left, _y, Left + Width / 3, _y + Px), _paint);
            _y += Body * 0.7f;
            var small = text with { Size = Body * 0.88f, Leading = 1.5f };
            float mark = Body * 1.6f;
            foreach (var n in b.Children)
            {
                var lines = Flow(n.Runs.Append(new Run(" ↩", RunStyle.None, n.Href)), Width - mark, small);
                var marker = Flow([new Run(n.Marker + ".", RunStyle.None)], float.MaxValue, small with { Color = C(_st.Mute) })[0];
                Ensure(lines[0].H);
                _canvas!.DrawNamedDestinationAnnotation(new SKPoint(Left, _y), n.Id);
                Lines(lines, Left + mark, (i, l, top) =>
                {
                    if (i != 0) return;
                    var tmp = new Line { Ascent = l.Ascent, Descent = l.Descent, H = l.H };
                    tmp.Pieces.AddRange(marker.Pieces);
                    DrawLine(tmp, Left, top);
                });
                _y += Body * 0.15f;
            }
        }

        /// <summary>Картинка отдельным абзацем: вписывается в ширину страницы. false — файла нет или он не читается.</summary>
        bool DrawImage(Run run)
        {
            if ((Export.ImagePath(run.Href!, _baseDir) ?? Export.ImagePath(run.Href!, _pageDir)) is not { } path) return false;
            using var image = SKImage.FromEncodedData(path);
            if (image == null) return false;
            float w = image.Width * Px, h = image.Height * Px, max = PageH - 2 * Margin;
            if (w > Width) { h *= Width / w; w = Width; }
            if (h > max) { w *= max / h; h = max; }
            Ensure(h);
            _canvas!.DrawImage(image, new SKRect(Left, _y, Left + w, _y + h), new SKSamplingOptions(SKCubicResampler.Mitchell));
            _y += h;
            return true;
        }
    }
}
