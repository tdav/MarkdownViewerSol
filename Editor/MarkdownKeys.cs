using Avalonia.Input;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using Leaflet.Core;

namespace Leaflet.Editor;

/// <summary>
/// Tab / Shift+Tab в разметке. В таблице — переход по ячейкам (в конце последней строки — новая строка),
/// исходник при этом выравнивается в ровные столбцы; всё одним шагом отмены. В пункте списка — вложить глубже / поднять.
/// </summary>
public sealed class MarkdownKeys(TextEditor editor, LivePreview preview)
{
    TextDocument Doc => editor.Document;

    /// <summary>Вызывается из OnKeyDown окна раньше редактора; true — клавиша обработана.</summary>
    public bool OnKey(KeyEventArgs e)
    {
        if (e.Key != Key.Tab || e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift)) return false;
        var area = editor.TextArea;
        if (!area.IsKeyboardFocusWithin || editor.IsReadOnly) return false;
        bool back = e.KeyModifiers == KeyModifiers.Shift;
        int n = area.Caret.Line;
        var sel = area.Selection;
        if (!sel.IsEmpty && Doc.GetLineByOffset(sel.SurroundingSegment.Offset).LineNumber != Doc.GetLineByOffset(sel.SurroundingSegment.EndOffset).LineNumber)
            return false; // несколько строк — обычный отступ редактора
        if (preview.Blocks.IsTable(n)) { TableTab(n, back); e.Handled = true; return true; }
        var line = Doc.GetLineByNumber(n);
        if (ListItem.Parse(Doc, line) is { } item) { Indent(line, item, back); e.Handled = true; return true; }
        return false;
    }

    // ---------- таблица ----------

    /// <summary>Выравнивает таблицу под курсором; курсор остаётся в той же ячейке.</summary>
    public bool AlignTable()
    {
        int n = editor.TextArea.Caret.Line;
        if (!preview.Blocks.IsTable(n)) return false;
        var (r, c) = CellAt(n);
        Rewrite(n, rows => (r, c));
        return true;
    }

    void TableTab(int n, bool back)
    {
        var (r, c) = CellAt(n);
        Rewrite(n, rows =>
        {
            int cols = MdTable.Split(rows[0]).Cells.Count;
            if (r == 1) return back ? (0, cols - 1) : rows.Count > 2 ? (2, 0) : (-1, 0); // с разделителя — в соседнюю строку
            int next = r == 0 ? 2 : r + 1;
            if (!back) return c + 1 < cols ? (r, c + 1) : next < rows.Count ? (next, 0) : (-1, 0);
            if (c > 0) return (r, c - 1);
            int prev = r - 1 == 1 ? 0 : r - 1;
            return r == 0 ? (0, 0) : (prev, cols - 1);
        });
    }

    /// <summary>Строка таблицы (от шапки) и ячейка, в которой стоит курсор.</summary>
    (int Row, int Cell) CellAt(int n)
    {
        var (first, _) = preview.Blocks.Table(n);
        var line = Doc.GetLineByNumber(n);
        var row = MdTable.Split(Doc.GetText(line));
        int col = editor.TextArea.Caret.Column - 1, cell = 0;
        for (int k = 0; k < row.Cells.Count; k++)
            if (col >= row.Cells[k].Start) cell = k;
        return (n - first, cell);
    }

    /// <summary>
    /// Таблица переписывается ровными столбцами (если что-то поменялось), курсор — в ячейку, которую вернёт <paramref name="target"/>
    /// по строкам ровной таблицы; строка −1 — добавить пустую строку в конец и встать в её ячейку.
    /// </summary>
    void Rewrite(int n, Func<List<string>, (int Row, int Cell)> target)
    {
        var (first, last) = preview.Blocks.Table(n);
        var firstLine = Doc.GetLineByNumber(first);
        var lastLine = Doc.GetLineByNumber(last);
        var lines = Enumerable.Range(first, last - first + 1).Select(k => Doc.GetText(Doc.GetLineByNumber(k))).ToList();
        var rows = MdTable.Format(lines);
        var (tr, tc) = target(rows);
        if (tr < 0)
        {
            var sep = MdTable.Split(rows[1]);
            rows.Add("|" + string.Concat(sep.Cells.Select(cell => new string(' ', cell.End - cell.Start) + "|")));
            tr = rows.Count - 1;
        }
        string nl = firstLine.DelimiterLength > 0 ? Doc.GetText(firstLine.EndOffset, firstLine.DelimiterLength) : "\n";
        string text = string.Join(nl, rows), old = Doc.GetText(firstLine.Offset, lastLine.EndOffset - firstLine.Offset);
        if (text != old) Doc.Replace(firstLine.Offset, old.Length, text); // одна правка — один шаг отмены
        var row = MdTable.Split(rows[tr]);
        int at = firstLine.Offset + rows.Take(tr).Sum(l => l.Length + nl.Length);
        if (tc < row.Cells.Count)
        {
            var cell = row.Cells[tc];
            at += cell.From < cell.To ? cell.To : Math.Min(cell.Start + 1, cell.End);
        }
        editor.TextArea.ClearSelection();
        editor.TextArea.Caret.Offset = at;
        editor.TextArea.Caret.BringCaretToView();
    }

    // ---------- список ----------

    /// <summary>
    /// Вложить пункт: отступ — до начала текста пункта выше того же уровня (как требует CommonMark), иначе на ширину маркера.
    /// Поднять: отступ — как у ближайшего пункта выше с меньшим отступом (или без отступа).
    /// </summary>
    void Indent(DocumentLine line, ListItem item, bool back)
    {
        string indent = Doc.GetText(line.Offset, item.IndentLength), wanted;
        if (back)
        {
            if (item.IndentLength == 0) return;
            wanted = "";
            for (var l = line.PreviousLine; l != null && line.LineNumber - l.LineNumber <= 200; l = l.PreviousLine)
                if (ListItem.Parse(Doc, l) is { } p && p.IndentColumns < item.IndentColumns) { wanted = Doc.GetText(l.Offset, p.IndentLength); break; }
                else if (l.Length > 0 && ListItem.Parse(Doc, l) == null && Doc.GetCharAt(l.Offset) is not (' ' or '\t')) break;
        }
        else
        {
            DocumentLine? sibling = null;
            for (var l = line.PreviousLine; l != null && line.LineNumber - l.LineNumber <= 200; l = l.PreviousLine)
            {
                if (l.Length == 0) continue;
                if (ListItem.Parse(Doc, l) is { } p)
                {
                    if (p.IndentColumns == item.IndentColumns) { sibling = l; break; }
                    if (p.IndentColumns < item.IndentColumns) break;
                }
                else if (Doc.GetCharAt(l.Offset) is not (' ' or '\t')) break;
            }
            string text = Doc.GetText(sibling ?? line);
            bool tabs = indent.Contains('\t');
            int content = new ListItem(item.IndentLength, item.IndentColumns, 0, false, '\0').ContentColumn(text) - item.IndentLength;
            wanted = indent + (tabs ? "\t" : new string(' ', Math.Max(2, content)));
        }
        if (wanted != indent) Doc.Replace(line.Offset, item.IndentLength, wanted);
    }
}
