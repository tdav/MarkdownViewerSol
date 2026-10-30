using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Leaflet.Core;

namespace Leaflet.Editor;

/// <summary>
/// Таблицы в живом просмотре. Строки без курсора рисуются сеткой целиком (<see cref="TableRowElement"/>):
/// ширина столбцов — по всей таблице настоящим шрифтом темы, выравнивание из строки-разделителя.
/// Строка с курсором — исходный текст, но каждая «|» заменена элементом с отступом (<see cref="PipeElement"/>) и стоит
/// ровно на линии сетки: отступы сначала оцениваются по ширине текста, потом поправляются по готовой раскладке строки
/// (второй проход — только этой строки). Шире текста — ячейки переносятся по словам, строка с курсором тогда как есть.
/// </summary>
public sealed class TablePreview
{
    readonly TextEditor _editor;
    readonly TextView _view;
    readonly LivePreview _preview;
    readonly Dictionary<int, TableLayout> _layouts = [];
    readonly Dictionary<string, double> _shown = [], _raw = [];
    (double Pipe, double Space, double Row, double Ascent)? _metrics;
    double _builtWidth;

    // Отступы «|» строки с курсором: оценка, потом поправка по раскладке.
    int _padLine = -1, _padRound;
    string? _padText;
    double[] _pads = [];
    bool _padLeading;

    /// <summary>Щелчок по ссылке в ячейке: цель и признак [[ссылки]].</summary>
    public event Action<string, bool>? LinkClicked;
    /// <summary>Сколько раз строку с курсором пришлось раскладывать вторично (для проверок).</summary>
    public int Realigns { get; private set; }

    public TablePreview(TextEditor editor, LivePreview preview)
    {
        _editor = editor;
        _view = editor.TextArea.TextView;
        _preview = preview;
        _view.ElementGenerators.Add(new TableGenerator(this, preview));
        _view.BackgroundRenderers.Add(new TableGrid(this, preview));
        _view.VisualLinesChanged += (_, _) => AlignActive();
        _view.SizeChanged += (_, e) =>
        {
            if (_layouts.Count > 0 && Math.Abs(e.NewSize.Width - _builtWidth) > 0.5) { _layouts.Clear(); _view.Redraw(); }
        };
        Skin.Changed += () => { _shown.Clear(); _raw.Clear(); _metrics = null; Reset(); };
        editor.TextArea.AddHandler(InputElement.PointerPressedEvent, OnPress, RoutingStrategies.Tunnel);
        preview.Clickable.Add(p => Hit(p) is { Link: not null });
    }

    public void Reset()
    {
        _layouts.Clear();
        _padLine = -1;
        _padText = null;
        foreach (var c in _collapsed.Values) c.Uncollapse();
        _collapsed.Clear();
        SyncCollapsed();
    }

    // ---------- строка-разделитель ----------
    // Строка не бывает ниже высоты шрифта, а «|---|» в сетке — лишь черта под шапкой: такие строки свёрнуты,
    // пока курсор не на шапке и не на самом разделителе (тогда видно исходник — двоеточия выравнивания).

    readonly Dictionary<int, CollapsedLineSection> _collapsed = [];

    // Секция умирает сама (смена документа, удалённая строка) — живость проверяется у неё.
    public bool IsCollapsed(int line) => _collapsed.TryGetValue(line, out var c) && c.IsCollapsed;

    internal void ActiveChanged(int old, int line)
    {
        var b = _preview.Blocks;
        if (old < 0 || b[old] is BlockMap.TableHead or BlockMap.TableSep || b[line] is BlockMap.TableHead or BlockMap.TableSep) SyncCollapsed();
    }

    void SyncCollapsed()
    {
        var doc = _editor.Document;
        if (doc == null) return;
        int active = _preview.ActiveLine;
        foreach (var dead in _collapsed.Where(p => !p.Value.IsCollapsed).Select(p => p.Key).ToList()) _collapsed.Remove(dead);
        foreach (int sep in _preview.Blocks.Separators)
        {
            bool hide = active != sep && active != sep - 1 && sep <= doc.LineCount;
            if (hide == _collapsed.ContainsKey(sep)) continue;
            if (hide)
            {
                var line = doc.GetLineByNumber(sep);
                _collapsed[sep] = _view.CollapseLines(line, line);
            }
            else
            {
                _collapsed[sep].Uncollapse();
                _collapsed.Remove(sep);
            }
            _view.Redraw(doc.GetLineByNumber(sep - 1));
        }
    }

    /// <summary>Правка без смены блоков: если в таблице поменялась ширина столбцов — перерисовать всю таблицу.</summary>
    internal void Edited(int offset)
    {
        var doc = _editor.Document;
        if (doc == null || offset > doc.TextLength) return;
        int n = doc.GetLineByOffset(offset).LineNumber;
        if (!_preview.Blocks.IsTable(n)) return;
        var (first, last) = _preview.Blocks.Table(n);
        _layouts.TryGetValue(first, out var old);
        var fresh = Build(first, last);
        _layouts[first] = fresh;
        if (old == null || old.Last != last || !old.X.AsSpan().SequenceEqual(fresh.X))
            for (int k = first; k <= last && k <= doc.LineCount; k++) _view.Redraw(doc.GetLineByNumber(k));
    }

    // ---------- раскладка ----------

    internal (double Pipe, double Space, double Row, double Ascent) Metrics()
    {
        if (_metrics is { } m) return m;
        var look = RichText.Look.Body;
        var line = RichText.Plain("Ag|", look);
        m = (RichText.Plain("|", look).WidthIncludingTrailingWhitespace, RichText.Plain(" ", look).WidthIncludingTrailingWhitespace,
            line.Height, line.TextLines[0].Baseline);
        _metrics = m;
        return m;
    }

    internal double TextWidth => _view.Bounds.Width > 50 ? _view.Bounds.Width : Math.Max(300, _editor.Bounds.Width - _editor.Padding.Left - _editor.Padding.Right);

    internal TableLayout Layout(int lineNumber)
    {
        var (first, last) = _preview.Blocks.Table(lineNumber);
        if (_layouts.TryGetValue(first, out var t) && t.Last == last) return t;
        return _layouts[first] = Build(first, last);
    }

    TableLayout Build(int first, int last)
    {
        var doc = _editor.Document;
        var (pipe, space, rowH, ascent) = Metrics();
        if (_shown.Count > 4000) { _shown.Clear(); _raw.Clear(); }
        var align = MdTable.Align(doc.GetText(doc.GetLineByNumber(first + 1)));
        var need = new List<double>();
        for (int n = first; n <= last; n++)
        {
            if (n == first + 1) continue;
            string text = doc.GetText(doc.GetLineByNumber(n));
            var row = MdTable.Split(text);
            for (int c = 0; c < row.Cells.Count; c++)
            {
                var cell = row.Cells[c];
                double w = Math.Max(Shown(MdTable.Unescape(text[cell.From..cell.To]), n == first) + 2 * space, Raw(text[cell.Start..cell.End]) + space);
                while (need.Count <= c) need.Add(space * 3);
                need[c] = Math.Max(need[c], w);
            }
        }
        while (need.Count < align.Length) need.Add(space * 3);
        int cols = need.Count;
        double width = TextWidth - 2, fixedW = (cols + 1) * pipe;
        _builtWidth = _view.Bounds.Width;
        bool fits = fixedW + need.Sum() <= width;
        if (!fits) Squeeze(need, Math.Max(cols * space * 4, width - fixedW), space * 4);
        var x = new double[cols + 1];
        for (int c = 0; c < cols; c++) x[c + 1] = x[c] + pipe + need[c];
        return new TableLayout
        {
            First = first, Last = last, X = x, Align = align, Fits = fits,
            PipeW = pipe, Space = space, RowHeight = rowH, Ascent = ascent, Width = x[cols] + pipe,
        };
    }

    /// <summary>Узкие столбцы остаются как есть, широкие делят остаток поровну.</summary>
    internal static void Squeeze(List<double> need, double total, double min)
    {
        var keep = new bool[need.Count];
        double rest = total;
        int open = need.Count;
        for (bool again = true; again && open > 0;)
        {
            again = false;
            double share = rest / open;
            for (int c = 0; c < need.Count; c++)
                if (!keep[c] && need[c] <= share) { keep[c] = true; rest -= need[c]; open--; again = true; }
        }
        for (int c = 0; c < need.Count; c++)
            if (!keep[c]) need[c] = Math.Max(min, rest / Math.Max(1, open));
    }

    double Shown(string text, bool header)
    {
        string key = (header ? "h" : "b") + text;
        if (_shown.TryGetValue(key, out var w)) return w;
        return _shown[key] = RichText.Layout(Inlines(text), CellLook(header)).WidthIncludingTrailingWhitespace;
    }

    double Raw(string text)
    {
        if (_raw.TryGetValue(text, out var w)) return w;
        return _raw[text] = RichText.Plain(text, RichText.Look.Body).WidthIncludingTrailingWhitespace;
    }

    internal static RichText.Look CellLook(bool header) => header ? new(Skin.EditorSize, Skin.Heading, Bold: true) : RichText.Look.Body;

    /// <summary>Оформление ячейки: [[ссылки]] — с пометкой «[[» в адресе (для перехода), как в живом просмотре — без проверки, есть ли заметка.</summary>
    internal static List<Run> Inlines(string text) => Export.Inlines(text, t => "[[" + t, ".md");

    // ---------- строки ----------

    internal VisualLineElement? Construct(ITextRunConstructionContext context, int offset)
    {
        var doc = context.Document;
        var line = doc.GetLineByOffset(offset);
        int n = line.LineNumber;
        var t = Layout(n);
        if (_preview.IsTableShown(n))
        {
            // Свёрнутый разделитель входит в строку шапки: свёрнутая строка не может начинать свою.
            int length = n == t.First && IsCollapsed(n + 1) ? line.TotalLength + line.NextLine.Length : line.Length;
            return new TableRowElement(t.Row(n, doc.GetText(line), n == t.First, n == t.First + 1), length);
        }
        string text = doc.GetText(line);
        var pads = ActivePads(t, n, text);
        int k = MdTable.Split(text).Pipes.IndexOf(offset - line.Offset);
        return new PipeElement(k >= 0 && k < pads.Length ? pads[k] : 0, t.PipeW);
    }

    /// <summary>Отступы перед «|» строки с курсором, чтобы черты встали на линии сетки (оценка по ширине текста).</summary>
    double[] ActivePads(TableLayout t, int n, string text)
    {
        if (_padLine == n && _padText == text) return _pads;
        var row = MdTable.Split(text);
        // Та же строка после правки: прошлые отступы ближе к правде, чем оценка.
        if (!(_padLine == n && _pads.Length == row.Pipes.Count && _padLeading == row.Leading))
        {
            var pads = new double[row.Pipes.Count];
            double cum = 0;
            for (int k = 0, from = 0; k < row.Pipes.Count; k++)
            {
                double start = cum + Raw(text[from..row.Pipes[k]]);
                pads[k] = Math.Max(0, Target(t, row.Leading, k) - start);
                cum = start + pads[k] + t.PipeW;
                from = row.Pipes[k] + 1;
            }
            _pads = pads;
        }
        _padLine = n;
        _padText = text;
        _padLeading = row.Leading;
        _padRound = 0;
        return _pads;
    }

    static double Target(TableLayout t, bool leading, int pipe) => t.X[Math.Min(leading ? pipe : pipe + 1, t.X.Length - 1)];

    /// <summary>Второй проход: черты строки с курсором стоят не там, где сетка (жирный, код) — поправить отступы и разложить строку ещё раз.</summary>
    void AlignActive()
    {
        int n = _preview.ActiveLine;
        if (_padLine != n || _padRound >= 2 || !_preview.Blocks.IsTable(n) || _view.GetVisualLine(n) is not { } vl || vl.TextLines.Count != 1) return;
        var pipes = vl.Elements.OfType<PipeElement>().ToList();
        if (pipes.Count != _pads.Length || pipes.Count == 0) return;
        var t = Layout(n);
        var pads = new double[pipes.Count];
        double shift = 0;
        bool changed = false;
        for (int k = 0; k < pipes.Count; k++)
        {
            double start = vl.GetVisualPosition(pipes[k].VisualColumn, VisualYPosition.LineTop).X + shift;
            pads[k] = Math.Max(0, Target(t, _padLeading, k) - start);
            shift += pads[k] - pipes[k].Pad;
            changed |= Math.Abs(pads[k] - pipes[k].Pad) > 0.25;
        }
        if (!changed) return;
        _pads = pads;
        _padRound++;
        Realigns++;
        _view.Redraw(vl);
    }

    /// <summary>Отступы черт строки с курсором после раскладки и где они должны быть (для проверок).</summary>
    public (double[] Actual, double[] Wanted)? ActivePipes()
    {
        int n = _preview.ActiveLine;
        if (!_preview.Blocks.IsTable(n) || _view.GetVisualLine(n) is not { } vl) return null;
        var t = Layout(n);
        var pipes = vl.Elements.OfType<PipeElement>().ToList();
        return (pipes.Select(p => vl.GetVisualPosition(p.VisualColumn, VisualYPosition.LineTop).X + p.Pad).ToArray(),
            pipes.Select((_, k) => Target(t, _padLeading, k)).ToArray());
    }

    // ---------- мышь ----------

    (RowView Row, VisualLine Line, double X, double Y, (string Target, bool Wiki)? Link)? Hit(Point p)
    {
        var vl = _view.GetVisualLineFromVisualTop(p.Y + _view.ScrollOffset.Y);
        if (vl?.Elements.OfType<TableRowElement>().FirstOrDefault() is not { } el) return null;
        double x = p.X + _view.ScrollOffset.X, y = p.Y + _view.ScrollOffset.Y - vl.VisualTop;
        if (x < 0 || x > el.Row.Table.Width || y > el.Row.Height) return null;
        return (el.Row, vl, x, y, el.Row.LinkAt(x, y));
    }

    void OnPress(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(_view);
        if (!point.Properties.IsLeftButtonPressed || e.ClickCount != 1 || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        if (Hit(point.Position) is not { } hit) return;
        e.Handled = true;
        if (hit.Link is { } link && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Dispatcher.UIThread.Post(() => LinkClicked?.Invoke(link.Target, link.Wiki));
            return;
        }
        // Курсор — в ячейку под указателем (строка раскроется в исходник после паузы двойного щелчка).
        var area = _editor.TextArea;
        area.ClearSelection();
        area.Caret.Offset = hit.Line.FirstDocumentLine.Offset + hit.Row.OffsetAt(hit.X, hit.Y);
        area.Focus();
    }
}

/// <summary>Раскладка одной таблицы: X[c] — левый край черты перед столбцом c (X[cols] — последняя черта).</summary>
public sealed class TableLayout
{
    public int First, Last;
    public double[] X = [];
    public char[] Align = [];
    public bool Fits;
    public double PipeW, Space, RowHeight, Ascent, Width;
    readonly Dictionary<int, RowView> _rows = [];

    public int Cols => X.Length - 1;

    internal RowView Row(int line, string text, bool header, bool separator)
    {
        if (_rows.TryGetValue(line, out var r) && r.Text == text) return r;
        return _rows[line] = new RowView(this, text, header, separator);
    }
}

/// <summary>Оформленная строка таблицы: ячейки разложены по столбцам, текст переносится, если столбец сужен.</summary>
public sealed class RowView
{
    public TableLayout Table { get; }
    public string Text { get; }
    public bool Header { get; }
    public bool Separator { get; }
    public double Height { get; }
    readonly MdTable.Row _row;
    readonly TextLayout?[] _cells;
    readonly List<RichText.Span>[] _spans;
    readonly string[] _shown;

    public RowView(TableLayout table, string text, bool header, bool separator)
    {
        Table = table;
        Text = text;
        Header = header;
        Separator = separator;
        _row = MdTable.Split(text);
        int cols = table.Cols;
        _cells = new TextLayout?[cols];
        _spans = new List<RichText.Span>[cols];
        _shown = new string[cols];
        if (separator) { Height = Math.Round(table.RowHeight * 0.35); return; }
        double h = table.RowHeight;
        for (int c = 0; c < cols; c++)
        {
            _spans[c] = [];
            _shown[c] = c < _row.Cells.Count ? MdTable.Unescape(_row.Text(text, c)) : "";
            if (_shown[c].Length == 0) continue;
            char a = c < table.Align.Length ? table.Align[c] : '\0';
            double inner = table.X[c + 1] - table.X[c] - table.PipeW - 2 * table.Space;
            _cells[c] = RichText.Layout(TablePreview.Inlines(_shown[c]), TablePreview.CellLook(header), Math.Max(4, inner),
                a == 'c' ? TextAlignment.Center : a == 'r' ? TextAlignment.Right : TextAlignment.Left, _spans[c]);
            h = Math.Max(h, _cells[c]!.Height);
        }
        Height = h;
    }

    double CellX(int c) => Table.X[c] + Table.PipeW + Table.Space;

    int Column(double x)
    {
        for (int c = 0; c < Table.Cols; c++)
            if (x < Table.X[c + 1] + Table.PipeW / 2) return c;
        return Table.Cols - 1;
    }

    public void Draw(DrawingContext dc, Point origin)
    {
        for (int c = 0; c < _cells.Length; c++)
            _cells[c]?.Draw(dc, new Point(origin.X + CellX(c), origin.Y));
    }

    /// <summary>Ссылка под точкой (координаты от начала строки).</summary>
    public (string Target, bool Wiki)? LinkAt(double x, double y)
    {
        if (Separator) return null;
        int c = Column(x);
        if (_cells[c] is not { } layout || RichText.RunAt(layout, _spans[c], new Point(x - CellX(c), y)) is not { } run) return null;
        if (!run.Style.HasFlag(RunStyle.Link) || run.Href == null) return null;
        return run.Href.StartsWith("[[") ? (run.Href[2..], true) : (run.Href, false);
    }

    /// <summary>Позиция в строке исходника для точки: в ячейке под указателем (без разметки — у того же знака, иначе в конце текста).</summary>
    public int OffsetAt(double x, double y)
    {
        int c = Column(x);
        if (Separator || c >= _row.Cells.Count) return Text.Length;
        var cell = _row.Cells[c];
        if (cell.From == cell.To) return Math.Min(cell.Start + 1, cell.End);
        string raw = Text[cell.From..cell.To];
        if (_cells[c] is { } layout && _shown[c] == raw)
            return cell.From + Math.Clamp(layout.HitTestPoint(new Point(x - CellX(c), y)).TextPosition, 0, raw.Length);
        return cell.To;
    }
}

/// <summary>Строка таблицы без курсора: строится вся, или «|» строки с курсором.</summary>
sealed class TableGenerator(TablePreview owner, LivePreview preview) : VisualLineElementGenerator
{
    public override int GetFirstInterestedOffset(int startOffset)
    {
        var doc = CurrentContext.Document;
        var line = doc.GetLineByOffset(startOffset);
        int n = line.LineNumber;
        if (!preview.Blocks.IsTable(n) || line.Length == 0) return -1;
        if (preview.IsTableShown(n)) return startOffset <= line.Offset ? line.Offset : -1;
        for (int o = startOffset; o < line.EndOffset; o++)
            if (doc.GetCharAt(o) == '|' && (o == line.Offset || doc.GetCharAt(o - 1) != '\\')) return o;
        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset) => owner.Construct(CurrentContext, offset);
}

/// <summary>Вся строка таблицы одним элементом (каретка в нём не останавливается — строка с курсором показывается исходником).</summary>
sealed class TableRowElement(RowView row, int length) : VisualLineElement(1, length)
{
    public RowView Row => row;
    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) => new RowRun(row, TextRunProperties);

    sealed class RowRun(RowView row, TextRunProperties properties) : DrawableTextRun
    {
        public override TextRunProperties? Properties => properties;
        public override int Length => 1;
        public override Size Size => new(row.Table.Width, row.Height);
        public override double Baseline => row.Separator ? row.Height : row.Table.Ascent;
        public override void Draw(DrawingContext drawingContext, Point origin) => row.Draw(drawingContext, origin);
    }
}

/// <summary>«|» строки с курсором: отступ до линии сетки и сама черта линией.</summary>
sealed class PipeElement(double pad, double pipe) : VisualLineElement(1, 1)
{
    public double Pad => pad;
    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) => new PipeRun(pad, pipe, TextRunProperties);

    sealed class PipeRun(double pad, double pipe, TextRunProperties properties) : DrawableTextRun
    {
        public override TextRunProperties? Properties => properties;
        public override int Length => 1;
        public override Size Size => new(pad + pipe, properties.FontRenderingEmSize);
        public override double Baseline => properties.FontRenderingEmSize * 0.8;
        public override void Draw(DrawingContext dc, Point origin) { } // черту рисует TableGrid по раскладке строки
    }
}

/// <summary>Сетка таблиц: фон шапки, горизонтальные линии строк, вертикальные — по раскладке (у строки с курсором — по её чертам).</summary>
sealed class TableGrid(TablePreview owner, LivePreview preview) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView view, DrawingContext dc)
    {
        if (!view.VisualLinesValid) return;
        foreach (var vl in view.VisualLines)
        {
            int n = vl.FirstDocumentLine.LineNumber;
            byte kind = preview.Blocks[n];
            if (kind < BlockMap.TableHead) continue;
            var t = owner.Layout(n);
            double y = Math.Round(vl.VisualTop - view.ScrollOffset.Y), h = Math.Round(vl.Height), x0 = -view.ScrollOffset.X;
            double right = x0 + Math.Round(t.X[^1] + t.PipeW / 2) + 1, left = x0 + Math.Round(t.X[0] + t.PipeW / 2);
            if (kind == BlockMap.TableHead)
            {
                dc.FillRectangle(Skin.CodeBack, new Rect(left, y, right - left, h));
                dc.FillRectangle(Skin.Line, new Rect(left, y, right - left, 1));
                dc.FillRectangle(Skin.Line, new Rect(left, y + h - 1, right - left, 1));
            }
            else if (kind == BlockMap.TableSep) dc.FillRectangle(Skin.Line, new Rect(left, y + Math.Floor(h / 2), right - left, 1));
            else if (kind == BlockMap.TableRow) dc.FillRectangle(Skin.Line, new Rect(left, y + h - 1, right - left, 1));
            if (n == preview.ActiveLine && vl.TextLines.Count == 1)
            {
                // Строка с курсором: линии там, где на деле стоят её черты.
                foreach (var p in vl.Elements.OfType<PipeElement>())
                {
                    double px = vl.GetVisualPosition(p.VisualColumn, VisualYPosition.LineTop).X + p.Pad + t.PipeW / 2;
                    dc.FillRectangle(Skin.Line, new Rect(x0 + Math.Round(px), y, 1, h));
                }
                continue;
            }
            if (n == preview.ActiveLine) continue;
            foreach (double x in t.X) dc.FillRectangle(Skin.Line, new Rect(x0 + Math.Round(x + t.PipeW / 2), y, 1, h));
        }
    }
}
