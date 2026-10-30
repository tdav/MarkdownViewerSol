using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Leaflet.Editor;

/// <summary>Пункт списка в начале строки: отступ, маркер («-», «- [ ]», «1.»), состояние задачи.</summary>
public readonly record struct ListItem(int IndentLength, int IndentColumns, int MarkerLength, bool Ordered, char Task)
{
    public bool IsTask => Task != '\0';
    public bool Done => Task is 'x' or 'X';
    /// <summary>Ширина маркера с пробелом после него (столбец начала текста пункта).</summary>
    public int ContentColumn(string line)
    {
        int o = IndentLength;
        while (o < line.Length && line[o] is not (' ' or '\t')) o++;
        return o + 1;
    }

    public static ListItem? Parse(TextDocument doc, DocumentLine line)
    {
        int o = line.Offset, end = line.EndOffset, cols = 0;
        while (o < end && doc.GetCharAt(o) is ' ' or '\t') { cols += doc.GetCharAt(o) == '\t' ? 4 - cols % 4 : 1; o++; }
        if (o + 1 >= end) return null;
        int indent = o - line.Offset;
        char c = doc.GetCharAt(o);
        if (c is '-' or '*' or '+')
        {
            if (doc.GetCharAt(o + 1) != ' ') return null;
            if (o + 5 <= end && doc.GetCharAt(o + 2) == '[' && doc.GetCharAt(o + 4) == ']' && doc.GetCharAt(o + 3) is ' ' or 'x' or 'X')
                return new ListItem(indent, cols, 5, false, doc.GetCharAt(o + 3));
            return new ListItem(indent, cols, 1, false, '\0');
        }
        int d = o;
        while (d < end && d - o < 9 && char.IsAsciiDigit(doc.GetCharAt(d))) d++;
        if (d == o || d + 1 >= end || doc.GetCharAt(d) is not ('.' or ')') || doc.GetCharAt(d + 1) != ' ') return null;
        return new ListItem(indent, cols, 0, true, '\0'); // номер остаётся текстом
    }

    /// <summary>Уровень вложенности: сколько пунктов выше с меньшим отступом (до абзаца без отступа, не дальше 200 строк).</summary>
    public static int Level(TextDocument doc, DocumentLine line, int columns)
    {
        int level = 0, need = columns;
        for (var l = line.PreviousLine; l != null && need > 0 && line.LineNumber - l.LineNumber <= 200; l = l.PreviousLine)
        {
            if (l.Length == 0) continue;
            if (Parse(doc, l) is { } p)
            {
                if (p.IndentColumns < need) { level++; need = p.IndentColumns; }
            }
            else if (doc.GetCharAt(l.Offset) is not (' ' or '\t')) break;
        }
        return level;
    }
}

/// <summary>Маркеры списков на оформленных строках: отступ по уровню вложенности, «•◦▪» по уровню, ☐/☑ у задач.</summary>
sealed class ListMarkerGenerator(LivePreview owner) : VisualLineElementGenerator
{
    public override int GetFirstInterestedOffset(int startOffset)
    {
        var doc = CurrentContext.Document;
        var line = doc.GetLineByOffset(startOffset);
        if (startOffset > line.Offset || !owner.IsRendered(line.LineNumber)) return -1;
        return ListItem.Parse(doc, line) is { } item && (item.MarkerLength > 0 || item.IndentLength > 0) ? line.Offset : -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        var doc = CurrentContext.Document;
        var line = doc.GetLineByOffset(offset);
        if (ListItem.Parse(doc, line) is not { } item) return null;
        return new ListMarkerElement(item, item.IndentColumns > 0 ? ListItem.Level(doc, line, item.IndentColumns) : 0);
    }
}

/// <summary>Отступ и маркер одним элементом. Считается пробелом: перенесённые строки пункта встают под его текст.</summary>
sealed class ListMarkerElement(ListItem item, int level) : VisualLineElement(1, item.IndentLength + item.MarkerLength)
{
    static readonly string[] Bullets = ["•", "◦", "▪"];
    public ListItem Item => item;
    public int Level => level;
    /// <summary>Где нарисован маркер (от начала элемента): по нему щелчок переключает задачу.</summary>
    public double MarkX { get; private set; }
    public double MarkW { get; private set; }

    public static double Step => Skin.EditorSize * 1.5;

    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
    {
        var p = TextRunProperties;
        string? glyph = item.IsTask ? (item.Done ? "☑" : "☐") : item.MarkerLength > 0 ? Bullets[level % Bullets.Length] : null;
        var brush = item.Done ? Skin.Accent : Skin.Mute;
        var text = glyph == null ? null : new FormattedText(glyph, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Skin.Symbols, p.FontRenderingEmSize, brush);
        var metrics = new FormattedText("x", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, p.Typeface, p.FontRenderingEmSize, brush);
        MarkX = level * Step;
        MarkW = text?.WidthIncludingTrailingWhitespace ?? 0;
        // Текст пункта — с ровного шага: у «•», «◦», «☐» разная ширина, а столбец текста на уровне один.
        double end = text == null ? MarkX : MarkX + Math.Max(Step, MarkW + p.FontRenderingEmSize * 0.4);
        return new MarkerRun(text, MarkX, end, metrics.Height, metrics.Baseline, p);
    }

    public override bool IsWhitespace(int visualColumn) => true;

    sealed class MarkerRun(FormattedText? text, double x, double width, double height, double baseline, TextRunProperties properties) : DrawableTextRun
    {
        public override TextRunProperties? Properties => properties;
        public override int Length => 1;
        public override Size Size => new(width, height);
        public override double Baseline => baseline;
        public override void Draw(DrawingContext dc, Point origin)
        {
            if (text != null) dc.DrawText(text, new Point(origin.X + x, origin.Y + baseline - text.Baseline));
        }
    }
}

/// <summary>Щелчок по ☐/☑ на оформленной строке переключает задачу: одна правка — один шаг отмены, курсор не сдвигается.</summary>
sealed class TaskClicks
{
    readonly TextEditor _editor;
    readonly TextView _view;

    public TaskClicks(TextEditor editor, LivePreview preview)
    {
        _editor = editor;
        _view = editor.TextArea.TextView;
        editor.TextArea.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            var p = e.GetCurrentPoint(_view);
            if (!p.Properties.IsLeftButtonPressed || e.KeyModifiers != KeyModifiers.None || editor.IsReadOnly) return;
            if (Hit(p.Position) is not { } hit) return;
            e.Handled = true;
            Toggle(hit.Line, hit.Item);
        }, RoutingStrategies.Tunnel);
        preview.Clickable.Add(p => Hit(p) != null);
    }

    (DocumentLine Line, ListItem Item)? Hit(Point p)
    {
        var vl = _view.GetVisualLineFromVisualTop(p.Y + _view.ScrollOffset.Y);
        if (vl == null || vl.Elements.OfType<ListMarkerElement>().FirstOrDefault() is not { Item.IsTask: true } el) return null;
        double x = p.X + _view.ScrollOffset.X - vl.GetVisualPosition(el.VisualColumn, VisualYPosition.LineTop).X;
        double y = p.Y + _view.ScrollOffset.Y - vl.VisualTop;
        return x >= el.MarkX - 2 && x <= el.MarkX + el.MarkW + 2 && y <= vl.TextLines[0].Height ? (vl.FirstDocumentLine, el.Item) : null;
    }

    public void Toggle(DocumentLine line, ListItem item)
    {
        int at = line.Offset + item.IndentLength + 3;
        _editor.Document.Replace(at, 1, item.Done ? " " : "x");
    }
}
