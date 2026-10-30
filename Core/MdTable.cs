using System.Text;
using System.Text.RegularExpressions;

namespace Leaflet.Core;

/// <summary>Таблицы Markdown (GFM): ячейки строки, выравнивание, приведение исходника к ровным столбцам.</summary>
public static class MdTable
{
    public static readonly Regex Separator = new(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$", RegexOptions.Compiled);

    /// <summary>Ячейка: [Start, End) — между чертами, [From, To) — текст без пробелов по краям (индексы в строке).</summary>
    public readonly record struct Cell(int Start, int End, int From, int To);

    public sealed class Row
    {
        public bool Leading, Trailing;
        public readonly List<int> Pipes = [];   // позиции неэкранированных «|»
        public readonly List<Cell> Cells = [];
        public string Text(string line, int c) => c < Cells.Count ? line[Cells[c].From..Cells[c].To] : "";
    }

    public static Row Split(string line)
    {
        var row = new Row();
        for (int i = 0; i < line.Length; i++)
            if (line[i] == '|' && (i == 0 || line[i - 1] != '\\')) row.Pipes.Add(i);
        int first = 0, last = line.Length;
        while (first < last && char.IsWhiteSpace(line[first])) first++;
        while (last > first && char.IsWhiteSpace(line[last - 1])) last--;
        row.Leading = row.Pipes.Count > 0 && row.Pipes[0] == first;
        row.Trailing = row.Pipes.Count > 0 && row.Pipes[^1] == last - 1 && !(row.Leading && row.Pipes.Count == 1);
        var bounds = new List<int>();
        if (!row.Leading) bounds.Add(-1);
        bounds.AddRange(row.Pipes);
        if (!row.Trailing) bounds.Add(line.Length);
        for (int k = 0; k + 1 < bounds.Count; k++)
        {
            int s = bounds[k] + 1, e = bounds[k + 1], f = s, t = e;
            while (f < t && char.IsWhiteSpace(line[f])) f++;
            while (t > f && char.IsWhiteSpace(line[t - 1])) t--;
            row.Cells.Add(new Cell(s, e, f, t));
        }
        return row;
    }

    public static bool IsSeparator(string line) => line.Contains('-') && Separator.IsMatch(line);

    /// <summary>Строка — шапка таблицы: в ней есть «|», следующая — разделитель с тем же числом столбцов.</summary>
    public static bool IsHeader(string line, string next) =>
        line.Contains('|') && IsSeparator(next) && Split(line).Cells.Count == Split(next).Cells.Count;

    /// <summary>Выравнивание столбцов по строке-разделителю: 'l', 'c', 'r'; '\0' — не задано (влево).</summary>
    public static char[] Align(string separator)
    {
        var row = Split(separator);
        return row.Cells.Select(c =>
        {
            string s = separator[c.From..c.To];
            bool l = s.StartsWith(':'), r = s.EndsWith(':') && s.Length > 1;
            return l && r ? 'c' : r ? 'r' : l ? 'l' : '\0';
        }).ToArray();
    }

    /// <summary>
    /// Таблица с ровными столбцами (для моноширинного шрифта): ширина по самому длинному тексту, «| » и « |» по краям,
    /// разделитель из «-» той же ширины с двоеточиями выравнивания. <paramref name="lines"/>[1] — разделитель.
    /// </summary>
    public static List<string> Format(IReadOnlyList<string> lines)
    {
        var rows = lines.Select(Split).ToList();
        var align = Align(lines[1]);
        int cols = Math.Max(align.Length, rows.Max(r => r.Cells.Count));
        var width = new int[cols];
        Array.Fill(width, 3);
        for (int r = 0; r < rows.Count; r++)
            if (r != 1)
                for (int c = 0; c < rows[r].Cells.Count; c++) width[c] = Math.Max(width[c], Width(rows[r].Text(lines[r], c)));
        var result = new List<string>(lines.Count);
        var sb = new StringBuilder();
        for (int r = 0; r < rows.Count; r++)
        {
            sb.Clear().Append('|');
            for (int c = 0; c < cols; c++)
            {
                char a = c < align.Length ? align[c] : '\0';
                if (r == 1)
                {
                    int w = width[c];
                    sb.Append(' ').Append(a is 'l' or 'c' ? ":" : "-").Append('-', w - 2).Append(a is 'r' or 'c' ? ":" : "-").Append(" |");
                    continue;
                }
                string text = rows[r].Text(lines[r], c);
                int pad = width[c] - Width(text), left = a == 'r' ? pad : a == 'c' ? pad / 2 : 0;
                sb.Append(' ').Append(' ', left).Append(text).Append(' ', pad - left).Append(" |");
            }
            result.Add(sb.ToString());
        }
        return result;
    }

    /// <summary>Ширина текста в знакоместах моноширинного шрифта: иероглифы и эмодзи — 2, комбинируемые знаки — 0.</summary>
    public static int Width(string s)
    {
        int w = 0;
        foreach (var r in s.EnumerateRunes())
        {
            int cp = r.Value;
            var cat = Rune.GetUnicodeCategory(r);
            if (cat is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.EnclosingMark || cp is 0x200D or 0xFE0F) continue;
            w += cp is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF
                or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1FAFF or >= 0x20000 ? 2 : 1;
        }
        return w;
    }

    /// <summary>Текст ячейки для показа: «\|» → «|».</summary>
    public static string Unescape(string cell) => cell.Replace("\\|", "|");
}
