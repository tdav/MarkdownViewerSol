using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Leaflet.Core;
using Leaflet.UI;
using SkiaSharp;
using LogicalDirection = AvaloniaEdit.Document.LogicalDirection;

namespace Leaflet.Editor;

/// <summary>
/// Картинки под строкой со ссылкой ![](…) / ![[…]]: в конец строки ставится элемент шириной во весь текст,
/// поэтому он всегда переносится на свой ряд. Разметку на оформленной строке прячет <see cref="LivePreview"/>,
/// на строке с курсором видно исходный текст, картинка остаётся под ним.
/// Размер берётся из заголовка файла сразу (высота строки известна до загрузки), сами пиксели декодируются в фоне
/// сразу в нужную ширину; кэш ограничен по памяти.
/// </summary>
public sealed class ImagePreview
{
    public const double MaxHeight = 420, Gap = 6;

    readonly TextEditor _editor;
    readonly TextView _view;
    readonly Func<string, bool, string?> _resolve;
    readonly Func<string, Block?> _embed;
    readonly Func<string?> _pageDir;
    readonly Dictionary<string, (NoteView View, double Width, int Version)> _notes = [];
    int _version;
    bool _redrawQueued;
    double _builtWidth;

    /// <summary>Щелчок по имени встроенной заметки: цель ![[…]] как написана.</summary>
    public event Action<string>? OpenNote;

    public ImageCache Cache { get; } = new();
    /// <summary>Ряды картинок в последней раскладке (для проверок).</summary>
    public List<ImageStrip> Built { get; } = [];

    /// <param name="resolve">Путь вставки (как написан, без %XX) и признак ![[…]] → полный путь файла или null.</param>
    /// <param name="embed">Цель ![[Заметка#раздел]] → блок встроенной заметки (см. <see cref="Export.Embedder"/>) или null.</param>
    /// <param name="pageDir">Папка открытой заметки (пути картинок встроенной заметки).</param>
    public ImagePreview(TextEditor editor, LivePreview preview, Func<string, bool, string?> resolve, Func<string, Block?> embed, Func<string?> pageDir)
    {
        _editor = editor;
        _view = editor.TextArea.TextView;
        _resolve = resolve;
        _embed = embed;
        _pageDir = pageDir;
        editor.DocumentChanged += (_, _) => _notes.Clear();
        Skin.Changed += () => _notes.Clear();
        editor.TextArea.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(_view).Properties.IsLeftButtonPressed || NoteHeaderAt(e.GetPosition(_view)) is not { } target) return;
            e.Handled = true;
            Dispatcher.UIThread.Post(() => OpenNote?.Invoke(target));
        }, RoutingStrategies.Tunnel);
        preview.Clickable.Add(p => NoteHeaderAt(p) != null);
        // Сначала метка конца текста (каретка в конце строки остаётся в ряду текста), за ней ряд картинок.
        _view.ElementGenerators.Add(new EndMarkerGenerator(this, preview));
        _view.ElementGenerators.Add(new ImageGenerator(this, preview));
        Cache.Loaded += QueueRedraw;
        var area = editor.TextArea;
        area.AddHandler(InputElement.KeyDownEvent, (_, e) => _lastKey = e.Key, RoutingStrategies.Tunnel, handledEventsToo: true);
        area.AddHandler(InputElement.PointerPressedEvent, (_, _) => _lastKey = Key.None, RoutingStrategies.Tunnel, handledEventsToo: true);
        area.Caret.PositionChanged += (_, _) => SkipImageRow();
        // Ширина при раскладке берётся из прошлого размера: после изменения ширины ряды пересчитываются ещё раз.
        _view.SizeChanged += (_, e) =>
        {
            if (Built.Count > 0 && Math.Abs(e.NewSize.Width - _builtWidth) > 0.5) QueueRedraw();
        };
        _view.VisualLinesChanged += (_, _) => Built.RemoveAll(s => !s.Alive(_view));
    }

    Key _lastKey;
    int _prevLine, _prevColumn;

    /// <summary>
    /// Каретка не стоит в ряду картинки: стрелка вниз проходит его насквозь (на следующую строку),
    /// стрелка вверх, щелчок по картинке, End — ставят каретку в конец текста строки.
    /// </summary>
    void SkipImageRow()
    {
        var caret = _editor.TextArea.Caret;
        int line = caret.Line;
        var doc = _editor.Document;
        // Ряд строки строим сами: живой просмотр только что мог сбросить ряды старой и новой активной строки.
        // Сначала дешёвая проверка «![» в строке: Caret.VisualColumn сам строит ряд строки (дорого на каждом нажатии).
        if (line <= doc.LineCount && doc.GetLineByNumber(line) is var dl && HasMark(doc, dl) && caret.VisualColumn > 0
            && _view.GetOrConstructVisualLine(dl) is { } vl
            && vl.Elements.OfType<EndMarkerElement>().FirstOrDefault() is { } marker && caret.VisualColumn > marker.VisualColumn)
        {
            if (_lastKey == Key.Down && _prevLine == line && line < doc.LineCount)
                caret.Position = new TextViewPosition(line + 1, Math.Min(_prevColumn, doc.GetLineByNumber(line + 1).Length + 1));
            else
                caret.Position = new TextViewPosition(line, caret.Column, marker.VisualColumn);
            return;
        }
        _prevLine = line;
        _prevColumn = caret.Column;
    }

    ImageStrip? _pending;
    int _pendingOffset = -1;

    internal VisualLineElement? ConstructMarker(string lineText, int lineOffset)
    {
        _pending = Build(lineText, lineOffset);
        _pendingOffset = lineOffset;
        return _pending == null ? null : new EndMarkerElement();
    }

    internal VisualLineElement? ConstructStrip(int lineOffset)
    {
        var strip = _pendingOffset == lineOffset ? _pending : null;
        _pending = null;
        _pendingOffset = -1;
        return strip == null ? null : new ImageStripElement(strip);
    }

    internal static int Interest(ITextRunConstructionContext context, LivePreview preview, int startOffset)
    {
        var doc = context.Document;
        var line = doc.GetLineByOffset(startOffset);
        return preview.Blocks[line.LineNumber] == BlockMap.Text && HasMark(doc, line) ? line.EndOffset : -1;
    }

    /// <summary>В строке есть «![» — быстрая проверка без выделения строки.</summary>
    static bool HasMark(TextDocument doc, DocumentLine line)
    {
        for (int o = line.Offset, end = line.EndOffset; o + 1 < end; o++)
            if (doc.GetCharAt(o) == '!' && doc.GetCharAt(o + 1) == '[') return true;
        return false;
    }

    /// <summary>Заметки изменились (сохранение, правка снаружи): встроенные пересобираются, если такие видны.</summary>
    public void NotesChanged()
    {
        _version++;
        if (Built.Any(s => s.Items.Any(i => i.Note != null))) QueueRedraw();
    }

    /// <summary>Цель встроенной заметки, если точка (в координатах TextView) — на строке с её именем.</summary>
    string? NoteHeaderAt(Point p)
    {
        var vl = _view.GetVisualLineFromVisualTop(p.Y + _view.ScrollOffset.Y);
        if (vl?.Elements.OfType<ImageStripElement>().FirstOrDefault() is not { } el) return null;
        // Ряд картинок — последний ряд строки: его верх — сумма высот рядов текста над ним.
        double top = vl.TextLines.Take(vl.TextLines.Count - 1).Sum(l => l.Height);
        double x = p.X + _view.ScrollOffset.X, y = p.Y + _view.ScrollOffset.Y - vl.VisualTop - top;
        foreach (var item in el.Strip.Items)
            if (item.Note is { } note && y >= item.Top && y < item.Top + note.HeaderHeight && x >= 0 && x < note.Width) return item.Src;
        return null;
    }

    /// <summary>Файлы изменились снаружи: забыть их в кэше и перерисовать, если такие видны.</summary>
    public void FilesChanged(IEnumerable<string> paths)
    {
        bool any = false;
        foreach (var p in paths)
            if (Vault.IsImage(p)) { Cache.Forget(p); any = true; }
        if (any) QueueRedraw();
    }

    public void QueueRedraw()
    {
        if (_redrawQueued) return;
        _redrawQueued = true;
        Dispatcher.UIThread.Post(() => { _redrawQueued = false; _view.Redraw(); }, DispatcherPriority.Render);
    }

    internal double TextWidth => _view.Bounds.Width > 50 ? _view.Bounds.Width : Math.Max(300, _editor.Bounds.Width - _editor.Padding.Left - _editor.Padding.Right);
    internal double Scaling => TopLevel.GetTopLevel(_editor)?.RenderScaling ?? 1;

    internal ImageStrip? Build(string lineText, int lineOffset)
    {
        var embeds = Markdown.Embeds(lineText);
        if (embeds.Count == 0) return null;
        double width = TextWidth, scaling = Scaling;
        _builtWidth = width;
        var items = new List<ImageStrip.Item>();
        foreach (var m in embeds)
        {
            var (src, wiki) = Markdown.EmbedSource(m);
            if (wiki && Markdown.IsNoteEmbed(src) && Note(src, width - 2, scaling) is { } note)
            {
                items.Add(new ImageStrip.Item { Src = src, Note = note, Size = new Size(width - 2, note.Height) });
                continue;
            }
            string? path = _resolve(src, wiki);
            var item = new ImageStrip.Item { Src = src, Path = path };
            if (path != null && Vault.IsImage(path) && Cache.Size(path) is { } px)
            {
                double w = px.Width / scaling, h = px.Height / scaling;
                double k = Math.Min(1, Math.Min((width - 2) / w, MaxHeight / h));
                item.Size = new Size(Math.Max(1, Math.Round(w * k)), Math.Max(1, Math.Round(h * k)));
                item.DecodeWidth = Math.Max(1, (int)Math.Ceiling(item.Size.Width * scaling));
            }
            else
            {
                item.Message = path == null ? Loc.T("img.nofile", src) : Loc.T("img.fail", Path.GetFileName(path));
                item.Size = new Size(width - 2, Skin.EditorSize * 1.6);
            }
            items.Add(item);
        }
        var strip = new ImageStrip(this, items, width - 1, lineOffset);
        Built.Add(strip);
        return strip;
    }

    /// <summary>Встроенная заметка: свёрстанный вид из кэша (пока не сохранялись заметки и не менялась ширина) или новый.</summary>
    NoteView? Note(string target, double width, double scaling)
    {
        if (_notes.TryGetValue(target, out var c) && c.Version == _version && Math.Abs(c.Width - width) < 0.5) return c.View;
        if (_embed(target) is not { } block) return null;
        var view = new NoteView(block, width, Cache, scaling, _pageDir());
        _notes[target] = (view, width, _version);
        return view;
    }
}

/// <summary>Картинки одной строки, друг под другом.</summary>
public sealed class ImageStrip
{
    public sealed class Item
    {
        public string Src = "";
        public string? Path, Message;
        public NoteView? Note;
        public Size Size;
        public double Top;   // от верха ряда
        public int DecodeWidth;
    }

    readonly ImagePreview _owner;
    public IReadOnlyList<Item> Items { get; }
    public double Width { get; }
    public double Height { get; }
    public int LineOffset { get; }
    public bool Drawn { get; private set; }

    public ImageStrip(ImagePreview owner, List<Item> items, double width, int lineOffset)
    {
        _owner = owner;
        Items = items;
        Width = width;
        LineOffset = lineOffset;
        double y = ImagePreview.Gap;
        foreach (var i in items) { i.Top = y; y += i.Size.Height + ImagePreview.Gap; }
        Height = y;
    }

    internal bool Alive(TextView view) => view.VisualLinesValid && view.VisualLines.Any(v =>
        v.Elements.OfType<ImageStripElement>().Any(e => e.Strip == this));

    public void Draw(DrawingContext dc, Point origin)
    {
        Drawn = true;
        double y = origin.Y + ImagePreview.Gap;
        foreach (var item in Items)
        {
            var rect = new Rect(origin.X, y, item.Size.Width, item.Size.Height);
            if (item.Note != null) item.Note.Draw(dc, rect.TopLeft);
            else if (item.Message != null)
            {
                dc.DrawRectangle(Skin.CodeBack, null, rect, 4, 4);
                var text = new FormattedText(item.Message, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(Skin.Font), Skin.EditorSize * 0.8, Skin.Mute);
                dc.DrawText(text, new Point(rect.X + 10, rect.Y + (rect.Height - text.Height) / 2));
            }
            else if (_owner.Cache.Get(item.Path!, item.DecodeWidth) is { } bmp)
                dc.DrawImage(bmp, new Rect(bmp.Size), rect);
            else
                dc.DrawRectangle(Skin.CodeBack, null, rect, 4, 4); // ещё грузится
            y += item.Size.Height + ImagePreview.Gap;
        }
    }
}

/// <summary>Ставит ряд картинок в конец строки, где есть вставки (кроме блоков кода).</summary>
sealed class ImageGenerator(ImagePreview owner, LivePreview preview) : VisualLineElementGenerator
{
    public override int GetFirstInterestedOffset(int startOffset) => ImagePreview.Interest(CurrentContext, preview, startOffset);

    public override VisualLineElement? ConstructElement(int offset) =>
        owner.ConstructStrip(CurrentContext.Document.GetLineByOffset(offset).Offset);
}

/// <summary>Разбирает строку и ставит метку конца текста; ряд картинок ставит следующий генератор.</summary>
sealed class EndMarkerGenerator(ImagePreview owner, LivePreview preview) : VisualLineElementGenerator
{
    public override int GetFirstInterestedOffset(int startOffset) => ImagePreview.Interest(CurrentContext, preview, startOffset);

    public override VisualLineElement? ConstructElement(int offset)
    {
        var doc = CurrentContext.Document;
        var line = doc.GetLineByOffset(offset);
        return owner.ConstructMarker(doc.GetText(line), line.Offset);
    }
}

/// <summary>
/// Метка нулевой ширины в конце текста строки: столбец конца текста принадлежит первому ряду,
/// поэтому каретка в конце строки рисуется после текста, а не перед картинкой.
/// </summary>
sealed class EndMarkerElement() : VisualLineElement(1, 0)
{
    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) => new EmptyRun(TextRunProperties);
    public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode) => -1;
    public override bool IsWhitespace(int visualColumn) => true;

    sealed class EmptyRun(TextRunProperties properties) : DrawableTextRun
    {
        public override TextRunProperties? Properties => properties;
        public override int Length => 1;
        public override Size Size => default;
        public override double Baseline => 0;
        public override void Draw(DrawingContext drawingContext, Point origin) { }
    }
}

/// <summary>Элемент нулевой длины в документе: каретка в нём не останавливается, текст строки не меняется.</summary>
sealed class ImageStripElement(ImageStrip strip) : VisualLineElement(1, 0)
{
    public ImageStrip Strip => strip;

    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
        new ImageStripRun(strip, TextRunProperties);

    public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode) => -1;
    public override bool IsWhitespace(int visualColumn) => true;
}

sealed class ImageStripRun(ImageStrip strip, TextRunProperties properties) : DrawableTextRun
{
    public override TextRunProperties? Properties => properties;
    public override int Length => 1;
    public override Size Size => new(strip.Width, strip.Height);
    public override double Baseline => strip.Height;
    public override void Draw(DrawingContext drawingContext, Point origin) => strip.Draw(drawingContext, origin);
}

/// <summary>
/// Кэш картинок: размер из заголовка (без декодирования), пиксели — в фоне, сразу уменьшенные до нужной ширины
/// (JPEG уменьшается ещё при декодировании). Предел — <see cref="Limit"/> байт, вытесняются давно не нужные.
/// </summary>
public sealed class ImageCache
{
    public const long Limit = 96L << 20;

    sealed class Entry
    {
        public PixelSize? Natural;
        public SKEncodedOrigin Origin;
        public DateTime Stamp, Checked;
        public Bitmap? Bitmap;
        public int Width, Wanted;
        public bool Loading;
        public LinkedListNode<string>? Node;
    }

    readonly Dictionary<string, Entry> _map = new(StringComparer.OrdinalIgnoreCase);
    readonly LinkedList<string> _lru = new();
    readonly SemaphoreSlim _decoders = new(2);
    long _bytes;

    public event Action? Loaded;
    public long Bytes => _bytes;
    public int Count => _map.Values.Count(e => e.Bitmap != null);
    public int Decodes { get; private set; }

    /// <summary>Размер картинки в пикселях с учётом поворота EXIF или null, если файл не читается.</summary>
    public PixelSize? Size(string path) => Fresh(path).Natural;

    Entry Fresh(string path)
    {
        var now = DateTime.UtcNow;
        if (_map.TryGetValue(path, out var e) && now - e.Checked < TimeSpan.FromSeconds(1)) return e;
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(path); } catch (Exception) { stamp = default; }
        if (e != null && e.Stamp == stamp) { e.Checked = now; return e; }
        if (e != null) Drop(path, e);
        e = new Entry { Stamp = stamp, Checked = now };
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec != null)
            {
                e.Origin = codec.EncodedOrigin;
                var (w, h) = (codec.Info.Width, codec.Info.Height);
                e.Natural = Swaps(e.Origin) ? new PixelSize(h, w) : new PixelSize(w, h);
            }
        }
        catch (Exception) { }
        _map[path] = e;
        return e;
    }

    static bool Swaps(SKEncodedOrigin o) => o is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    /// <summary>Картинка шириной не меньше <paramref name="width"/> (или пока та, что есть); нужной нет — грузится в фоне, потом <see cref="Loaded"/>.</summary>
    public Bitmap? Get(string path, int width)
    {
        var e = Fresh(path);
        if (e.Natural is not { } nat) return null;
        width = Math.Min(width, nat.Width);
        if (e.Bitmap != null)
        {
            _lru.Remove(e.Node!);
            _lru.AddFirst(e.Node!);
        }
        if ((e.Bitmap == null || e.Width < width) && !(e.Loading && e.Wanted >= width))
        {
            e.Loading = true;
            e.Wanted = Math.Min(nat.Width, (width + 63) / 64 * 64); // с запасом: мелкие изменения ширины не перегружают
            _ = Load(path, e, e.Wanted);
        }
        return e.Bitmap;
    }

    async Task Load(string path, Entry e, int width)
    {
        await _decoders.WaitAsync();
        Bitmap? bmp;
        try { bmp = await Task.Run(() => Decode(path, width, e.Origin)); }
        finally { _decoders.Release(); }
        Dispatcher.UIThread.Post(() =>
        {
            if (!_map.TryGetValue(path, out var cur) || cur != e) { bmp?.Dispose(); return; }
            if (e.Wanted == width) e.Loading = false;
            if (bmp == null) return;
            Decodes++;
            if (e.Bitmap != null) { _bytes -= Weight(e.Bitmap); e.Bitmap.Dispose(); _lru.Remove(e.Node!); }
            e.Bitmap = bmp;
            e.Width = bmp.PixelSize.Width;
            e.Node = _lru.AddFirst(path);
            _bytes += Weight(bmp);
            while (_bytes > Limit && _lru.Last is { } last && last != e.Node)
            {
                _lru.RemoveLast();
                if (_map.TryGetValue(last.Value, out var old) && old.Bitmap != null)
                {
                    _bytes -= Weight(old.Bitmap);
                    old.Bitmap.Dispose();
                    old.Bitmap = null;
                    old.Width = 0;
                }
            }
            Loaded?.Invoke();
        });
    }

    static long Weight(Bitmap b) => (long)b.PixelSize.Width * b.PixelSize.Height * 4;

    public void Forget(string path)
    {
        if (_map.TryGetValue(path, out var e)) Drop(path, e);
    }

    void Drop(string path, Entry e)
    {
        if (e.Bitmap != null)
        {
            _bytes -= Weight(e.Bitmap);
            e.Bitmap.Dispose();
            _lru.Remove(e.Node!);
        }
        _map.Remove(path);
    }

    /// <summary>Декодирует сразу в уменьшенный размер (JPEG — силами кодека), доводит до ширины и поворачивает по EXIF.</summary>
    static Bitmap? Decode(string path, int width, SKEncodedOrigin origin)
    {
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec == null) return null;
            var info = codec.Info;
            bool swap = Swaps(origin);
            int naturalW = swap ? info.Height : info.Width;
            float scale = Math.Min(1f, width / (float)naturalW);
            var dims = codec.GetScaledDimensions(scale);
            var target = new SKImageInfo(dims.Width, dims.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var decoded = new SKBitmap(target);
            var result = codec.GetPixels(target, decoded.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                decoded.Dispose();
                decoded = SKBitmap.Decode(path);
                if (decoded == null) return null;
            }
            // Довести до нужной ширины (до поворота ширина — это высота, если картинка лежит на боку).
            int tw = Math.Max(1, (int)Math.Round((swap ? info.Height : info.Width) * scale));
            int th = Math.Max(1, (int)Math.Round((swap ? info.Width : info.Height) * scale));
            int rw = swap ? th : tw, rh = swap ? tw : th;
            if (decoded.Width > rw * 1.02 || decoded.Height > rh * 1.02)
            {
                var resized = decoded.Resize(new SKImageInfo(rw, rh, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                if (resized != null) { decoded.Dispose(); decoded = resized; }
            }
            if (origin is not (SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default))
            {
                var turned = Orient(decoded, origin);
                decoded.Dispose();
                decoded = turned;
            }
            using (decoded)
                return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, decoded.GetPixels(),
                    new PixelSize(decoded.Width, decoded.Height), new Vector(96, 96), decoded.RowBytes);
        }
        catch (Exception) { return null; }
    }

    static SKBitmap Orient(SKBitmap src, SKEncodedOrigin origin)
    {
        bool swap = Swaps(origin);
        var dst = new SKBitmap(new SKImageInfo(swap ? src.Height : src.Width, swap ? src.Width : src.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(dst);
        float w = dst.Width, h = dst.Height;
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Translate(w, 0); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight: canvas.Translate(w, h); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft: canvas.Translate(0, h); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop: canvas.Scale(-1, 1); canvas.RotateDegrees(90); break; // транспонирование
            case SKEncodedOrigin.RightTop: canvas.Translate(w, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(w, h); canvas.Scale(1, -1); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, h); canvas.RotateDegrees(-90); break;
        }
        canvas.DrawBitmap(src, 0, 0);
        return dst;
    }
}
