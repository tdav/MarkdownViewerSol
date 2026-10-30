using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Leaflet.Core;

namespace Leaflet.Editor;

/// <summary>
/// Живой просмотр Markdown: разметка скрыта везде, кроме строки с курсором.
/// Скрытие — шрифт почти нулевого размера и прозрачный цвет: текст остаётся в документе,
/// поэтому выделение, копирование и отмена работают с исходным Markdown.
/// </summary>
public sealed class LivePreview : DocumentColorizingTransformer
{
    const double HiddenSize = 0.01;

    static readonly Regex Heading = Markdown.Heading;
    static readonly Regex Quote = new(@"^[ \t]{0,3}>[ \t]?", RegexOptions.Compiled);
    static readonly Regex TaskItem = new(@"^[ \t]*([-*+] \[[ xX]\])", RegexOptions.Compiled);
    static readonly Regex Rule =new(@"^ {0,3}([-*_])( *\1){2,} *$", RegexOptions.Compiled);
    static readonly Regex CodeSpan = new(@"(`+)(?!`)(.+?)(?<!`)\1(?!`)", RegexOptions.Compiled);
    static readonly Regex WikiLink = Markdown.WikiLink;
    static readonly Regex Link = Markdown.Link;
    static readonly Regex BoldItalic = new(@"(\*\*\*|___)(?=\S)(.+?)(?<=\S)\1", RegexOptions.Compiled);
    static readonly Regex Bold = new(@"(\*\*|__)(?=\S)(.+?)(?<=\S)\1", RegexOptions.Compiled);
    static readonly Regex ItalicStar = new(@"(?<![*\w\\])\*(?=[^\s*])(.+?)(?<=[^\s*])\*(?![*\w])", RegexOptions.Compiled);
    static readonly Regex ItalicUnderscore = new(@"(?<![_\w])_(?=[^\s_])(.+?)(?<=[^\s_])_(?![_\w])", RegexOptions.Compiled);
    static readonly Regex Strike = new(@"~~(?=\S)(.+?)(?<=\S)~~", RegexOptions.Compiled);
    static readonly Regex Mark = new(@"==(?=\S)(.+?)(?<=\S)==", RegexOptions.Compiled);
    static readonly Regex Tag = Markdown.Tag;

    readonly TextEditor _editor;
    readonly TextView _view;
    readonly DispatcherTimer _clickPause;
    readonly List<(int Start, int End)> _protected = [];
    TextDocument? _document;
    int _activeLine;
    bool _pointerDown, _readMode;

    public BlockMap Blocks { get; } = new();
    public TablePreview Tables { get; }
    /// <summary>Проверки «под указателем можно щёлкнуть» (задача, ссылка в таблице, имя встроенной заметки): указатель — «рука».</summary>
    public List<Func<Point, bool>> Clickable { get; } = [];
    public int ActiveLine => _activeLine;

    /// <summary>Тема «Терминал»: «# » у заголовков видно приглушённым, как на макете.</summary>
    public bool ShowHeadingMarks { get; set; }

    /// <summary>Режим чтения: оформлены все строки, включая строку с курсором.</summary>
    public bool ReadMode
    {
        get => _readMode;
        set
        {
            if (_readMode == value) return;
            _readMode = value;
            _activeLine = 0;
            if (!value) UpdateActiveLine();
            Tables.ActiveChanged(-1, _activeLine);
            _view.Redraw();
        }
    }

    public LivePreview(TextEditor editor)
    {
        _editor = editor;
        _view = editor.TextArea.TextView;
        _view.LineTransformers.Add(this);
        _view.ElementGenerators.Add(new ListMarkerGenerator(this));
        _view.BackgroundRenderers.Add(new CodeBlockBackground(this));
        Tables = new TablePreview(editor, this);
        _ = new TaskClicks(editor, this);

        // Пока идёт работа мышью (и ещё время двойного щелчка после), строку не раскрываем:
        // иначе текст сдвинется под указателем и второй щелчок попадёт не туда.
        _clickPause = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _clickPause.Tick += (_, _) => { _clickPause.Stop(); UpdateActiveLine(); };
        var area = editor.TextArea;
        area.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(area).Properties.IsLeftButtonPressed) _pointerDown = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        area.AddHandler(InputElement.PointerReleasedEvent, (_, _) =>
        {
            if (!_pointerDown) return;
            _pointerDown = false;
            _clickPause.Stop();
            _clickPause.Start();
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        area.AddHandler(InputElement.KeyDownEvent, (_, _) => _clickPause.Stop(),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        area.AddHandler(InputElement.TextInputEvent, (_, _) => _clickPause.Stop(),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        area.Caret.PositionChanged += (_, _) => UpdateActiveLine();

        editor.DocumentChanged += (_, _) => Attach(editor.Document);
        Attach(editor.Document);
    }

    public bool IsQuote(DocumentLine line)
    {
        var doc = _document!;
        int o = line.Offset, end = line.EndOffset;
        while (o < end && o - line.Offset < 3 && doc.GetCharAt(o) == ' ') o++;
        return o < end && doc.GetCharAt(o) == '>';
    }

    /// <summary>Строка показывается оформленной (разметка скрыта).</summary>
    public bool IsRendered(int lineNumber) => lineNumber != _activeLine && Blocks[lineNumber] == BlockMap.Text;

    /// <summary>Строку таблицы видно сеткой (не строка с курсором).</summary>
    public bool IsTableShown(int lineNumber) => lineNumber != _activeLine && Blocks.IsTable(lineNumber);

    void Attach(TextDocument? document)
    {
        if (_document != null) _document.Changed -= OnDocumentChanged;
        _document = document;
        if (document == null) return;
        document.Changed += OnDocumentChanged;
        Blocks.Rebuild(document);
        Tables.Reset();
        _activeLine = 0;
        UpdateActiveLine();
        _view.Redraw();
    }

    void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        // Границы блоков кода и таблиц меняются только при вводе/удалении `, |, перевода строки,
        // а «-» и «:» — только в строке с «|» (строка-разделитель таблицы).
        var doc = _document!;
        int a = Affects(e.InsertedText), b = Affects(e.RemovedText);
        bool structure = a == 2 || b == 2;
        if (!structure && (a == 1 || b == 1))
        {
            var line = doc.GetLineByOffset(Math.Min(e.Offset, doc.TextLength));
            structure = Blocks.IsTable(line.LineNumber) || Blocks.IsTable(line.LineNumber + 1) || HasPipe(doc, line);
        }
        if (structure)
        {
            if (Blocks.Rebuild(doc)) { Tables.Reset(); _view.Redraw(); }
            else Tables.Edited(e.Offset);
        }
        else Tables.Edited(e.Offset);
    }

    /// <returns>2 — может поменять блоки, 1 — только если рядом таблица, 0 — нет.</returns>
    static int Affects(ITextSource text)
    {
        int r = 0;
        for (int i = 0; i < text.TextLength; i++)
            switch (text.GetCharAt(i))
            {
                case '`' or '\n' or '\r' or '|': return 2;
                case '-' or ':': r = 1; break;
            }
        return r;
    }

    static bool HasPipe(TextDocument doc, DocumentLine line)
    {
        for (int o = line.Offset; o < line.EndOffset; o++)
            if (doc.GetCharAt(o) == '|') return true;
        return false;
    }

    void UpdateActiveLine()
    {
        if (_pointerDown || _clickPause.IsEnabled || _readMode) return;
        int line = _editor.TextArea.Caret.Line;
        if (line == _activeLine) return;
        int old = _activeLine;
        _activeLine = line;
        RedrawLine(old);
        RedrawLine(line);
        Tables.ActiveChanged(old, line);
    }

    void RedrawLine(int lineNumber)
    {
        var doc = _document;
        if (doc != null && lineNumber >= 1 && lineNumber <= doc.LineCount)
            _view.Redraw(doc.GetLineByNumber(lineNumber));
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        int n = line.LineNumber;
        int start = line.Offset, end = line.EndOffset;
        if (line.Length == 0) return;

        var kind = Blocks[n];
        if (kind >= BlockMap.TableHead)
        {
            // Строку таблицы без курсора рисует TablePreview целиком; на строке с курсором — исходник с оформлением.
            if (n == _activeLine) Inline(CurrentContext.Document.GetText(line), start, 0, true);
            return;
        }
        if (kind != BlockMap.Text)
        {
            ChangeLinePart(start, end, e =>
            {
                e.TextRunProperties.SetTypeface(Skin.Mono);
                e.TextRunProperties.SetFontRenderingEmSize(Skin.MonoSize);
                if (kind == BlockMap.Fence) e.TextRunProperties.SetForegroundBrush(Skin.Markup);
            });
            return;
        }

        bool active = n == _activeLine;
        string text = CurrentContext.Document.GetText(line);
        int body = 0;

        Match m;
        if ((m = Heading.Match(text)).Success)
        {
            double size = Skin.HeadingSize[m.Groups[1].Length];
            ChangeLinePart(start, end, e =>
            {
                e.TextRunProperties.SetFontRenderingEmSize(size);
                SetWeight(e, FontWeight.Bold);
                e.TextRunProperties.SetForegroundBrush(Skin.Heading);
            });
            Hide(start, start + m.Length, active || ShowHeadingMarks);
            body = m.Length;
        }
        else if ((m = Quote.Match(text)).Success)
        {
            ChangeLinePart(start, end, e =>
            {
                SetStyle(e, FontStyle.Italic);
                e.TextRunProperties.SetForegroundBrush(Skin.QuoteText);
            });
            if (active) Hide(start, start + m.Length, true);
            else ChangeLinePart(start, start + m.Length, // «> » прозрачный, но занимает место под полосу
                e => e.TextRunProperties.SetForegroundBrush(Brushes.Transparent));
            body = m.Length;
        }
        else if (!active && (m = TaskItem.Match(text)).Success)
        {
            // Выполненная задача — приглушённой и зачёркнутой.
            int marker = start + m.Groups[1].Index;
            if (text[m.Groups[1].Index + 3] != ' ' && marker + 6 < end)
                ChangeLinePart(marker + 6, end, e =>
                {
                    e.TextRunProperties.SetForegroundBrush(Skin.Markup);
                    e.TextRunProperties.SetTextDecorations(TextDecorations.Strikethrough);
                });
        }
        else if (text.Length > 4 && text[0] == '[' && (m = Markdown.FootnoteDef.Match(text)).Success)
        {
            // Определение сноски: «[^» и «]:» скрыты, метка — цветом ссылки.
            Hide(start, start + 2, active);
            ChangeLinePart(start + 2, start + 2 + m.Groups[1].Length, e =>
            {
                e.TextRunProperties.SetForegroundBrush(Skin.Accent);
                SetWeight(e, FontWeight.Bold);
            });
            Hide(start + 2 + m.Groups[1].Length, start + 4 + m.Groups[1].Length, active);
            body = m.Length;
        }
        else if (Rule.IsMatch(text))
        {
            ChangeLinePart(start, end, e => e.TextRunProperties.SetForegroundBrush(Skin.Markup));
            return;
        }

        Inline(text, start, body, active);
    }

    void Inline(string text, int lineStart, int from, bool active)
    {
        _protected.Clear();

        foreach (Match m in CodeSpan.Matches(text, from))
        {
            int o = lineStart + m.Index, len = m.Groups[1].Length;
            Hide(o, o + len, active);
            ChangeLinePart(o + len, o + m.Length - len, e =>
            {
                e.TextRunProperties.SetTypeface(Skin.Mono);
                e.TextRunProperties.SetFontRenderingEmSize(Skin.MonoSize);
                e.TextRunProperties.SetBackgroundBrush(Skin.CodeBack);
            });
            Hide(o + m.Length - len, o + m.Length, active);
            _protected.Add((m.Index, m.Index + m.Length));
        }

        // Ссылка на сноску [^1]: видна только метка, мельче и выше строки.
        if (text.Contains("[^"))
            foreach (Match m in Markdown.FootnoteRef.Matches(text, from))
            {
                if (IsProtected(m)) continue;
                int o = lineStart + m.Index;
                Hide(o, o + 2, active);
                ChangeLinePart(o + 2, o + m.Length - 1, e =>
                {
                    e.TextRunProperties.SetForegroundBrush(Skin.Accent);
                    e.TextRunProperties.SetFontRenderingEmSize(e.TextRunProperties.FontRenderingEmSize * 0.72);
                    e.TextRunProperties.SetBaselineAlignment(BaselineAlignment.Superscript);
                });
                Hide(o + m.Length - 1, o + m.Length, active);
                _protected.Add((m.Index, m.Index + m.Length));
            }

        // Вставка картинки: сама картинка — под строкой (ImagePreview), разметка видна только на строке с курсором.
        if (text.Contains("!["))
            foreach (Match m in Markdown.Embed.Matches(text, from))
            {
                if (IsProtected(m)) continue;
                Hide(lineStart + m.Index, lineStart + m.Index + m.Length, active);
                _protected.Add((m.Index, m.Index + m.Length));
            }

        foreach (Match m in WikiLink.Matches(text, from))
        {
            if (IsProtected(m)) continue;
            int o = lineStart + m.Index;
            var alias = m.Groups[2];
            int shownStart = alias.Success ? lineStart + alias.Index + 1 : o + 2;
            Hide(o, shownStart, active);
            ChangeLinePart(shownStart, o + m.Length - 2, StyleLink);
            Hide(o + m.Length - 2, o + m.Length, active);
            _protected.Add((m.Index, m.Index + m.Length));
        }

        foreach (Match m in Link.Matches(text, from))
        {
            if (IsProtected(m)) continue;
            int o = lineStart + m.Index;
            int textEnd = lineStart + m.Groups[1].Index + m.Groups[1].Length;
            Hide(o, o + 1, active);
            ChangeLinePart(o + 1, textEnd, StyleLink);
            Hide(textEnd, o + m.Length, active);
            _protected.Add((m.Groups[2].Index - 2, m.Index + m.Length));
        }

        Wrap(BoldItalic, text, lineStart, from, 3, active, e =>
        {
            SetWeight(e, FontWeight.Bold);
            SetStyle(e, FontStyle.Italic);
        });
        Wrap(Bold, text, lineStart, from, 2, active, e => SetWeight(e, FontWeight.Bold));
        Wrap(ItalicStar, text, lineStart, from, 1, active, e => SetStyle(e, FontStyle.Italic));
        Wrap(ItalicUnderscore, text, lineStart, from, 1, active, e => SetStyle(e, FontStyle.Italic));
        Wrap(Strike, text, lineStart, from, 2, active, e =>
        {
            e.TextRunProperties.SetTextDecorations(TextDecorations.Strikethrough);
            e.TextRunProperties.SetForegroundBrush(Skin.Markup);
        });
        Wrap(Mark, text, lineStart, from, 2, active, e => e.TextRunProperties.SetBackgroundBrush(Skin.Highlight));

        foreach (Match m in Tag.Matches(text, from))
        {
            if (IsProtected(m)) continue;
            ChangeLinePart(lineStart + m.Index, lineStart + m.Index + m.Length,
                e => e.TextRunProperties.SetForegroundBrush(Skin.Accent));
        }
    }

    void Wrap(Regex regex, string text, int lineStart, int from, int marker, bool active, Action<VisualLineElement> style)
    {
        foreach (Match m in regex.Matches(text, from))
        {
            if (IsProtected(m)) continue;
            int o = lineStart + m.Index;
            if (marker == 3) _protected.Add((m.Index, m.Index + m.Length));
            Hide(o, o + marker, active);
            ChangeLinePart(o + marker, o + m.Length - marker, style);
            Hide(o + m.Length - marker, o + m.Length, active);
        }
    }

    bool IsProtected(Match m)
    {
        foreach (var (s, e) in _protected)
            if (m.Index < e && s < m.Index + m.Length) return true;
        return false;
    }

    /// <summary>На активной строке разметка видна приглушённой, на остальных — скрыта.</summary>
    void Hide(int from, int to, bool active)
    {
        if (from >= to) return;
        ChangeLinePart(from, to, active
            ? e => e.TextRunProperties.SetForegroundBrush(Skin.Markup)
            : e =>
            {
                e.TextRunProperties.SetFontRenderingEmSize(HiddenSize);
                e.TextRunProperties.SetForegroundBrush(Brushes.Transparent);
                e.TextRunProperties.SetBackgroundBrush(null);
            });
    }

    static void StyleLink(VisualLineElement e)
    {
        e.TextRunProperties.SetForegroundBrush(Skin.Accent);
        e.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
    }

    static void SetWeight(VisualLineElement e, FontWeight weight)
    {
        var t = e.TextRunProperties.Typeface;
        e.TextRunProperties.SetTypeface(new Typeface(t.FontFamily, t.Style, weight));
    }

    static void SetStyle(VisualLineElement e, FontStyle style)
    {
        var t = e.TextRunProperties.Typeface;
        e.TextRunProperties.SetTypeface(new Typeface(t.FontFamily, style, t.Weight));
    }
}

/// <summary>Фон под блоками кода и полоса слева у цитат.</summary>
sealed class CodeBlockBackground(LivePreview owner) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView view, DrawingContext dc)
    {
        foreach (var vl in view.VisualLines)
        {
            var line = vl.FirstDocumentLine;
            double y = vl.VisualTop - view.ScrollOffset.Y;
            if (owner.Blocks.IsCode(line.LineNumber))
                dc.FillRectangle(Skin.CodeBack, new Rect(0, y, view.Bounds.Width, vl.Height));
            else if (owner.IsQuote(line))
                dc.FillRectangle(Skin.Accent, new Rect(0, y + 2, 3, vl.Height - 4));
        }
    }
}

