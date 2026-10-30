using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using Leaflet.Core;

namespace Leaflet.Editor;

/// <summary>
/// Переход по ссылкам: щелчок по ссылке на оформленной строке или Ctrl+щелчок на любой.
/// Сноски: щелчок по [^1] — к определению (над ссылкой — подсказка с текстом сноски), по метке определения — обратно к ссылке.
/// Над ссылкой, по которой можно перейти, указатель — «рука».
/// </summary>
public sealed class LinkClicks
{
    readonly TextEditor _editor;
    readonly LivePreview _preview;
    Point _pressAt;
    bool _armed, _pressCtrl;
    string? _tip;

    /// <summary>Цель [[ссылки]] (как написана) или адрес обычной ссылки.</summary>
    public event Action<string, bool>? Clicked;

    public LinkClicks(TextEditor editor, LivePreview preview)
    {
        _editor = editor;
        _preview = preview;
        var area = editor.TextArea;
        area.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            var p = e.GetCurrentPoint(area);
            _armed = p.Properties.IsLeftButtonPressed && e.ClickCount == 1 && !e.Handled;
            _pressAt = p.Position;
            _pressCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            // Строку проверяем при нажатии: после щелчка она станет активной и раскроется.
            if (_armed) _armed = Hit(e.GetPosition(_editor), _pressCtrl) != null;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        area.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (!_armed) return;
            _armed = false;
            if (Vector.Distance(e.GetPosition(area), _pressAt) > 4 || !_editor.TextArea.Selection.IsEmpty) return;
            var link = Hit(e.GetPosition(_editor), _pressCtrl);
            if (link is { Footnote: { } note } l) Dispatcher.UIThread.Post(() => JumpFootnote(note, l.Definition));
            else if (link is { } k) Dispatcher.UIThread.Post(() => Clicked?.Invoke(k.Target, k.Wiki));
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        var view = area.TextView;
        view.PointerMoved += (_, e) =>
        {
            var hit = Hit(e.GetPosition(_editor), e.KeyModifiers.HasFlag(KeyModifiers.Control));
            bool over = hit != null || _preview.Clickable.Any(f => f(e.GetPosition(view)));
            view.Cursor = over ? HandCursor : null;
            ShowTip(hit is { Footnote: { } n, Definition: false } ? Definition(n) : null);
        };
        view.PointerExited += (_, _) => ShowTip(null);
    }

    static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    (string Target, bool Wiki, string? Footnote, bool Definition)? Hit(Point point, bool ctrl)
    {
        var pos = _editor.GetPositionFromPoint(point);
        if (pos is not { } p) return null;
        var doc = _editor.Document;
        if (p.Line < 1 || p.Line > doc.LineCount) return null;
        if (!ctrl && !_preview.IsRendered(p.Line)) return null;
        var line = doc.GetLineByNumber(p.Line);
        string text = doc.GetText(line);
        int col = p.Column - 1;
        // Вставка картинки: Ctrl+щелчок по разметке открывает файл (цель с «!»).
        foreach (var m in Markdown.Embeds(text))
            if (col >= m.Index && col < m.Index + m.Length)
                return ctrl && Markdown.EmbedSource(m) is var (src, wiki) ? ("!" + src, wiki, null, false) : null;
        if (text.Contains("[^"))
        {
            if (Markdown.FootnoteDef.Match(text) is { Success: true } d && col < d.Length) return ("", false, d.Groups[1].Value, true);
            foreach (Match m in Markdown.FootnoteRef.Matches(text))
                if (col >= m.Index && col <= m.Index + m.Length) return ("", false, m.Groups[1].Value, false);
        }
        foreach (Match m in Markdown.WikiLink.Matches(text))
            if (col >= m.Index && col < m.Index + m.Length) return (m.Groups[1].Value, true, null, false);
        foreach (Match m in Markdown.Link.Matches(text))
            if (col >= m.Index && col < m.Index + m.Length) return (m.Groups[2].Value, false, null, false);
        return null;
    }

    /// <summary>Строка определения сноски или первая строка со ссылкой на неё (вне блоков кода).</summary>
    DocumentLine? FindFootnote(string label, bool definition)
    {
        var doc = _editor.Document;
        string def = "[^" + label + "]:", reference = "[^" + label + "]";
        foreach (var line in doc.Lines)
        {
            if (line.Length < reference.Length || _preview.Blocks.IsCode(line.LineNumber)) continue;
            string text = doc.GetText(line);
            if (definition ? text.Contains(reference) && Markdown.FootnoteRef.Matches(text).Any(m => m.Groups[1].Value.Equals(label, StringComparison.OrdinalIgnoreCase))
                : text.StartsWith(def, StringComparison.OrdinalIgnoreCase)) return line;
        }
        return null;
    }

    string? Definition(string label)
    {
        if (FindFootnote(label, definition: false) is not { } line) return null;
        string text = _editor.Document.GetText(line);
        return text[Markdown.FootnoteDef.Match(text).Length..].Trim();
    }

    /// <summary>К определению сноски (с ссылки) или к ссылке (с определения): курсор — в начало текста.</summary>
    public void JumpFootnote(string label, bool fromDefinition)
    {
        if (FindFootnote(label, fromDefinition) is not { } line) return;
        string text = _editor.Document.GetText(line);
        int col = fromDefinition ? Markdown.FootnoteRef.Matches(text).First(m => m.Groups[1].Value.Equals(label, StringComparison.OrdinalIgnoreCase)).Index + 2 + label.Length + 1
            : Markdown.FootnoteDef.Match(text).Length;
        ShowTip(null);
        _editor.TextArea.Caret.Offset = line.Offset + Math.Min(col, line.Length);
        _editor.ScrollToLine(line.LineNumber);
        _editor.TextArea.Focus();
    }

    void ShowTip(string? text)
    {
        var view = _editor.TextArea.TextView;
        if (text == _tip) return;
        _tip = text;
        ToolTip.SetTip(view, text);
        ToolTip.SetIsOpen(view, text != null);
    }
}
