using System.Text.RegularExpressions;

namespace Leaflet.Core;

/// <summary>Разбор Markdown, общий для редактора и индекса.</summary>
public static class Markdown
{
    public static readonly Regex WikiLink = new(@"\[\[([^\[\]|\n]+)(\|[^\[\]\n]+)?\]\]", RegexOptions.Compiled);
    public static readonly Regex Link = new(@"(?<!!)\[([^\[\]\n]+)\]\(([^()\s]+)\)", RegexOptions.Compiled);
    public static readonly Regex Tag = new(@"(?<![\w#&])#[\p{L}\p{N}_/-]*\p{L}[\p{L}\p{N}_/-]*", RegexOptions.Compiled);
    public static readonly Regex Heading = new(@"^(#{1,6})[ \t]+", RegexOptions.Compiled);
    public static readonly Regex CodeSpan = new(@"(`+)(?!`)(.+?)(?<!`)\1(?!`)", RegexOptions.Compiled);
    /// <summary>Картинка: ![подпись](путь), ![подпись](&lt;путь с пробелами&gt;) — группы alt, src; ![[файл|подпись]] — группы wiki, alt.</summary>
    public static readonly Regex Embed = new(
        @"!\[(?<alt>[^\[\]\n]*)\]\((?:<(?<src>[^<>\n]+)>|(?<src>[^()\s]+))\)|!\[\[(?<wiki>[^\[\]|\n]+)(?:\|(?<alt>[^\[\]\n]*))?\]\]",
        RegexOptions.Compiled);

    /// <summary>Ссылка на сноску [^метка] (не определение «[^метка]:»).</summary>
    public static readonly Regex FootnoteRef = new(@"\[\^([^\[\]\s]+)\](?!:)", RegexOptions.Compiled);
    /// <summary>Определение сноски в начале строки: «[^метка]: текст».</summary>
    public static readonly Regex FootnoteDef = new(@"^\[\^([^\[\]\s]+)\]:[ \t]?", RegexOptions.Compiled);

    /// <summary>Вставка ![[…]] ведёт на заметку, а не на файл: нет расширения или .md.</summary>
    public static bool IsNoteEmbed(string target)
    {
        int hash = target.IndexOf('#');
        string name = (hash < 0 ? target : target[..hash]).Trim();
        return !Path.HasExtension(name) || name.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Раздел заметки: строка заголовка (по тексту без учёта регистра или по якорю) и всё до следующего заголовка того же или старшего уровня.
    /// null — такого заголовка нет.
    /// </summary>
    public static string? Section(string text, string heading)
    {
        heading = heading.Trim();
        string slug = Export.Slug(heading);
        var code = CodeRanges(text);
        int start = -1, level = 0;
        for (int pos = 0; pos < text.Length;)
        {
            int end = text.IndexOf('\n', pos);
            if (end < 0) end = text.Length;
            if (text[pos] == '#' && !code.Any(c => pos >= c.Start && pos < c.End) && Heading.Match(text, pos, end - pos) is { Success: true } m)
            {
                int lvl = m.Groups[1].Length;
                if (start >= 0 && lvl <= level) return text[start..pos].TrimEnd();
                string title = text[(pos + m.Length)..end].Trim();
                if (start < 0 && (title.Equals(heading, StringComparison.OrdinalIgnoreCase) || Export.Slug(title) == slug && slug.Length > 0))
                {
                    start = pos;
                    level = lvl;
                }
            }
            pos = end + 1;
        }
        return start < 0 ? null : text[start..].TrimEnd();
    }

    /// <summary>Вставки картинок в строке, кроме тех, что внутри `кода`.</summary>
    public static List<Match> Embeds(string line)
    {
        var list = new List<Match>();
        if (!line.Contains("![")) return list;
        MatchCollection? code = null;
        foreach (Match m in Embed.Matches(line))
        {
            code ??= line.Contains('`') ? CodeSpan.Matches(line) : null;
            if (code != null && code.Any(c => m.Index < c.Index + c.Length && c.Index < m.Index + m.Length)) continue;
            list.Add(m);
        }
        return list;
    }

    /// <summary>Путь вставки: для ![](…) — без экранирования %XX, для ![[…]] — как написан.</summary>
    public static (string Src, bool Wiki) EmbedSource(Match m) =>
        m.Groups["wiki"].Success ? (m.Groups["wiki"].Value.Trim(), true) : (Unescape(m.Groups["src"].Value), false);

    public static string Unescape(string s)
    {
        try { return s.Contains('%') ? Uri.UnescapeDataString(s) : s; }
        catch (UriFormatException) { return s; }
    }

    /// <summary>Ключ ссылки: «Папка/Имя#Раздел» → «папка/имя». Пустая строка — ссылка на себя или пустая.</summary>
    public static string LinkKey(string target)
    {
        int hash = target.IndexOf('#');
        if (hash >= 0) target = target[..hash];
        target = target.Trim().Replace('\\', '/');
        if (target.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) target = target[..^3];
        return target.ToLowerInvariant();
    }

    /// <summary>Ключи всех [[ссылок]] текста, без повторов.</summary>
    public static string[] LinkKeys(string text)
    {
        if (!text.Contains("[[")) return [];
        var keys = new HashSet<string>();
        foreach (Match m in WikiLink.Matches(text))
        {
            string key = LinkKey(m.Groups[1].Value);
            if (key.Length > 0) keys.Add(key);
        }
        return [.. keys];
    }

    /// <summary>Теги текста (с «#»): без блоков кода и без якорей адресов вроде «/#раздел».</summary>
    public static List<Match> TagMatches(string text)
    {
        var tags = new List<Match>();
        if (!text.Contains('#')) return tags;
        List<(int Start, int End)>? fences = null;
        foreach (Match m in Tag.Matches(text))
        {
            if (m.Index > 0 && text[m.Index - 1] == '/') continue;
            fences ??= CodeRanges(text);
            if (fences.Count > 0 && fences.Any(f => m.Index >= f.Start && m.Index < f.End)) continue;
            tags.Add(m);
        }
        return tags;
    }

    /// <summary>Теги заметки без «#», без повторов (регистр не различается).</summary>
    public static string[] TagKeys(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in TagMatches(text)) set.Add(m.Value[1..]);
        return [.. set];
    }

    /// <summary>Промежутки текста внутри оград ```.</summary>
    public static List<(int Start, int End)> CodeRanges(string text)
    {
        var ranges = new List<(int, int)>();
        int open = -1;
        for (int pos = 0; pos < text.Length;)
        {
            int end = text.IndexOf('\n', pos);
            if (end < 0) end = text.Length;
            int o = pos;
            while (o < end && o - pos < 3 && text[o] == ' ') o++;
            if (end - o >= 3 && text[o] == '`' && text[o + 1] == '`' && text[o + 2] == '`')
            {
                if (open < 0) open = pos;
                else { ranges.Add((open, end)); open = -1; }
            }
            pos = end + 1;
        }
        if (open >= 0) ranges.Add((open, text.Length));
        return ranges;
    }

    /// <summary>Заголовки текста (без блоков кода): уровень и текст. Заголовки, которые нельзя записать в ссылку ([ ] |), пропускаются.</summary>
    public static List<(int Level, string Text)> Headings(string text)
    {
        var list = new List<(int, string)>();
        var code = CodeRanges(text);
        for (int pos = 0; pos < text.Length;)
        {
            int end = text.IndexOf('\n', pos);
            if (end < 0) end = text.Length;
            if (text[pos] == '#')
            {
                var m = Heading.Match(text, pos, end - pos);
                if (m.Success && !code.Any(c => pos >= c.Start && pos < c.End))
                {
                    string title = text[(pos + m.Length)..end].Trim();
                    if (title.Length > 0 && title.IndexOfAny(['[', ']', '|']) < 0) list.Add((m.Groups[1].Length, title));
                }
            }
            pos = end + 1;
        }
        return list;
    }

    /// <summary>Строка текста вокруг позиции, обрезанная до <paramref name="max"/> символов.</summary>
    public static (string Text, int Start) LineAround(string text, int index, int length, int max = 110)
    {
        int ls = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        if (index == 0) ls = 0;
        int le = text.IndexOf('\n', index);
        if (le < 0) le = text.Length;
        int from = ls, to = le;
        if (to - from > max)
        {
            from = Math.Max(ls, index - max / 3);
            to = Math.Min(le, from + max);
        }
        string s = text[from..to].TrimEnd('\r');
        int lead = s.Length - s.TrimStart().Length;
        string trimmed = s.Trim();
        return ((from > ls ? "…" : "") + trimmed + (to < le ? "…" : ""), index - from - lead + (from > ls ? 1 : 0));
    }

    /// <summary>Строка без разметки ссылок: [[Цель|Текст]] → «Текст»; выделенный фрагмент сдвигается вместе с текстом.</summary>
    public static (string Text, int Start, int Length) Plain(string line, int start, int length)
    {
        var sb = new System.Text.StringBuilder(line.Length);
        int pos = 0, newStart = start, newEnd = start + length;
        foreach (Match m in WikiLink.Matches(line))
        {
            sb.Append(line, pos, m.Index - pos);
            string shown = m.Groups[2].Success ? m.Groups[2].Value[1..] : m.Groups[1].Value;
            int removed = m.Length - shown.Length;
            int outStart = sb.Length;
            sb.Append(shown);
            if (m.Index + m.Length <= start) { newStart -= removed; newEnd -= removed; }
            else if (m.Index <= start) { newStart = outStart; newEnd = outStart + shown.Length; }
            else if (m.Index < start + length) newEnd -= removed;
            pos = m.Index + m.Length;
        }
        sb.Append(line, pos, line.Length - pos);
        return (sb.ToString(), newStart, Math.Max(0, newEnd - newStart));
    }

    public static int CountWords(string text)
    {
        int words = 0;
        bool inWord = false;
        foreach (char c in text)
        {
            bool w = char.IsLetterOrDigit(c);
            if (w && !inWord) words++;
            inWord = w;
        }
        return words;
    }
}
