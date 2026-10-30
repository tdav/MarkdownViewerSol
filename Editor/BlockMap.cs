using AvaloniaEdit.Document;
using Leaflet.Core;

namespace Leaflet.Editor;

/// <summary>
/// Вид каждой строки по блокам: обычный текст, ограда ``` или строка внутри блока кода,
/// шапка таблицы, её строка-разделитель и строки таблицы (GFM: шапка с «|», под ней разделитель с тем же числом столбцов,
/// дальше — непустые строки с «|»).
/// </summary>
public sealed class BlockMap
{
    public const byte Text = 0, Fence = 1, Code = 2, TableHead = 3, TableSep = 4, TableRow = 5;
    byte[] _kind = [];

    public byte this[int lineNumber] => lineNumber >= 1 && lineNumber <= _kind.Length ? _kind[lineNumber - 1] : Text;
    public bool IsCode(int lineNumber) => this[lineNumber] is Fence or Code;
    public bool IsTable(int lineNumber) => this[lineNumber] >= TableHead;
    public int LineCount => _kind.Length;
    /// <summary>Строки-разделители таблиц (после <see cref="Rebuild"/>).</summary>
    public List<int> Separators { get; } = [];

    /// <summary>Первая и последняя строка таблицы, в которой стоит строка <paramref name="lineNumber"/>.</summary>
    public (int First, int Last) Table(int lineNumber)
    {
        int first = lineNumber, last = lineNumber;
        while (first > 1 && this[first] != TableHead) first--;
        while (last < _kind.Length && this[last + 1] is TableSep or TableRow) last++;
        return (first, last);
    }

    /// <returns>true, если разметка блоков изменилась.</returns>
    public bool Rebuild(TextDocument doc)
    {
        // Один проход по строке текста: обход doc.Lines с GetCharAt/IndexOf по строкам стоил ~1,2 мс на 5000 строк
        // на каждом Enter, проход по string — в разы меньше.
        string text = doc.Text;
        int count = doc.LineCount;
        var kind = new byte[count];
        var start = new int[count + 1];
        int n = 1;
        for (int o = 0; o < text.Length && n < count; o++)
        {
            char ch = text[o];
            if (ch == '\n') start[n++] = o + 1;
            else if (ch == '\r') { if (o + 1 < text.Length && text[o + 1] == '\n') o++; start[n++] = o + 1; }
        }
        start[count] = text.Length + 1;
        int End(int k) // конец строки без перевода
        {
            int e = k + 1 < count ? start[k + 1] : text.Length;
            if (k + 1 < count) { e--; if (e > start[k] && text[e - 1] == '\r' && text[e] == '\n') e--; }
            return e;
        }
        bool inside = false, pipes = text.Contains('|');
        var hasPipe = pipes ? new bool[count] : null;
        for (int k = 0; k < count; k++)
        {
            int s = start[k], e = End(k);
            if (IsFence(text, s, e)) { kind[k] = Fence; inside = !inside; }
            else if (inside) kind[k] = Code;
            if (hasPipe != null) hasPipe[k] = text.AsSpan(s, e - s).Contains('|');
        }
        if (hasPipe != null)
            for (int k = 0; k < count; k++)
            {
                if (kind[k] != Text || !hasPipe[k]) continue;
                if (k > 0 && kind[k - 1] is TableSep or TableRow) kind[k] = TableRow;
                else if (k + 1 < count && kind[k + 1] == Text && hasPipe[k + 1]
                    && MdTable.IsHeader(text[start[k]..End(k)], text[start[k + 1]..End(k + 1)]))
                {
                    kind[k] = TableHead;
                    kind[k + 1] = TableSep;
                }
            }
        bool changed = !kind.AsSpan().SequenceEqual(_kind);
        _kind = kind;
        Separators.Clear();
        for (int k = 0; k < kind.Length; k++) if (kind[k] == TableSep) Separators.Add(k + 1);
        return changed;
    }

    static bool IsFence(string text, int o, int end)
    {
        int s = o;
        while (o < end && o - s < 3 && text[o] == ' ') o++;
        return end - o >= 3 && text[o] == '`' && text[o + 1] == '`' && text[o + 2] == '`';
    }
}
