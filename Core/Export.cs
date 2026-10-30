using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Leaflet.Core;

[Flags]
public enum RunStyle { None = 0, Bold = 1, Italic = 2, Code = 4, Strike = 8, Mark = 16, Tag = 32, Link = 64, Missing = 128, Image = 256, Break = 512, Note = 1024 }

/// <summary>
/// Кусок строки с одним оформлением. У ссылки <see cref="Href"/> — адрес, у картинки — путь к файлу,
/// у ссылки на сноску (<see cref="RunStyle.Note"/>) — «#fn-N»; <see cref="Id"/> — якорь первой ссылки на сноску.
/// </summary>
public sealed record Run(string Text, RunStyle Style, string? Href = null, string? Id = null);

public enum BlockKind { Heading, Paragraph, Item, Quote, Code, Rule, Table, Embed, Footnotes }

/// <summary>
/// Блок заметки: заголовок, абзац (строки через <see cref="RunStyle.Break"/>), пункт списка, цитата, код, черта, таблица,
/// встроенная заметка ![[Имя]] (<see cref="Children"/> — её блоки), сноски в конце (<see cref="Children"/> — абзацы с номерами).
/// </summary>
public sealed class Block
{
    public BlockKind Kind;
    public int Level;              // заголовок: 1–6; пункт списка: глубина с 0
    public List<Run> Runs = [];
    public string Id = "";         // заголовок: якорь; сноска: «fn-N»
    public string Marker = "";     // пункт: «•», «3.», «☐», «☑»; сноска: номер
    public bool Ordered;
    public int Number;
    public string Code = "", Lang = "";
    public List<List<List<Run>>> Rows = []; // таблица: строки → ячейки → куски; первая строка — шапка
    public char[] Align = [];      // 'l', 'c', 'r'
    public List<Block> Children = [];
    public string Title = "";      // встраивание: имя заметки (› раздел)
    public string? Href;           // встраивание: ссылка на заметку; сноска: обратно к ссылке
    public string? BaseDir;        // встраивание: папка заметки (картинки в ней — от этой папки)
    public string Message = "";    // встраивание: почему не показано (нет заметки, по кругу)
}

/// <summary>Цвета (ARGB) и шрифты темы на момент экспорта; размеры в пикселях, как в редакторе.</summary>
public sealed record ExportStyle(
    uint Bg, uint Panel, uint Fg, uint Mute, uint Accent, uint CodeBack, uint Heading, uint QuoteText, uint Highlight, uint Line,
    string[] Fonts, string[] MonoFonts, double Size, double MonoSize, double[] HeadingSizes, bool HeadingMarks, int Width);

/// <summary>
/// Экспорт заметки: Markdown разбирается в блоки (<see cref="Parse"/>), из них собирается HTML-страница (<see cref="Html"/>)
/// или PDF (<see cref="PdfExport"/>). [[Ссылки]] становятся обычными ссылками на соседние файлы экспорта.
/// </summary>
public static class Export
{
    static readonly Regex Fence = new(@"^ {0,3}```\s*([^`\s]*)", RegexOptions.Compiled);
    static readonly Regex Quote = new(@"^ {0,3}>[ \t]?", RegexOptions.Compiled);
    static readonly Regex Item = new(@"^([ \t]*)([-*+]|\d{1,9}[.)])[ \t]+(?:\[([ xX])\][ \t]+)?", RegexOptions.Compiled);
    static readonly Regex Rule = new(@"^ {0,3}([-*_])( *\1){2,} *$", RegexOptions.Compiled);
    const int MaxEmbedDepth = 4;

    // Порядок важен: при совпадении в одной позиции побеждает первое (как в живом просмотре: код и ссылки раньше выделения).
    static readonly Regex[] Inline =
    [
        new(@"(`+)(?!`)(.+?)(?<!`)\1(?!`)", RegexOptions.Compiled),
        Markdown.Embed,
        Markdown.WikiLink,
        Markdown.Link,
        new(@"https?://[^\s<>()\[\]]*[^\s<>()\[\].,;:!?'""»]", RegexOptions.Compiled),
        new(@"(\*\*\*|___)(?=\S)(.+?)(?<=\S)\1", RegexOptions.Compiled),
        new(@"(\*\*|__)(?=\S)(.+?)(?<=\S)\1", RegexOptions.Compiled),
        new(@"(?<![*\w\\])\*(?=[^\s*])(.+?)(?<=[^\s*])\*(?![*\w])", RegexOptions.Compiled),
        new(@"(?<![_\w])_(?=[^\s_])(.+?)(?<=[^\s_])_(?![_\w])", RegexOptions.Compiled),
        new(@"~~(?=\S)(.+?)(?<=\S)~~", RegexOptions.Compiled),
        new(@"==(?=\S)(.+?)(?<=\S)==", RegexOptions.Compiled),
        Markdown.Tag,
        Markdown.FootnoteRef,
    ];
    static readonly Regex ImageLine = new(@"^\s*(?:(?:" + Markdown.Embed + @")\s*)+$", RegexOptions.Compiled);
    static readonly RunStyle[] Wraps = [RunStyle.Bold | RunStyle.Italic, RunStyle.Bold, RunStyle.Italic, RunStyle.Italic, RunStyle.Strike, RunStyle.Mark];

    // ---------- разбор ----------

    /// <summary>Состояние разбора одной заметки: ссылки, сноски (определения и номера по первому упоминанию).</summary>
    sealed class Ctx(Func<string, string?> link, string ext, string prefix)
    {
        public readonly Func<string, string?> Link = link;
        public readonly string Ext = ext, Prefix = prefix;
        public Dictionary<string, string>? Defs;
        public readonly List<string> Order = [];
        public readonly Dictionary<string, int> Numbers = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Блоки заметки. <paramref name="link"/> даёт адрес [[ссылки]] по её цели («Имя#Раздел») или null, если заметки нет.
    /// <paramref name="embed"/> даёт блок встроенной заметки по цели ![[Имя#раздел]] (null — это не заметка, а файл).
    /// <paramref name="prefix"/> — приставка якорей сносок (у встроенной заметки свои номера).
    /// </summary>
    public static List<Block> Parse(string text, Func<string, string?> link, string ext = ".html", Func<string, Block?>? embed = null, string prefix = "")
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = new List<Block>();
        var depths = new List<int>(); // отступы открытых уровней списка
        var ids = new HashSet<string>();
        var ctx = new Ctx(link, ext, prefix);
        Block? para = null;
        var skip = Footnotes(lines, ctx);
        List<Run> Inl(string s) { var runs = new List<Run>(); Inline1(s, RunStyle.None, null, runs, ctx); return runs; }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0 || skip[i]) { para = null; depths.Clear(); continue; }
            Match m;
            if ((m = Fence.Match(line)).Success)
            {
                var code = new StringBuilder();
                int j = i + 1;
                for (; j < lines.Length && !Fence.IsMatch(lines[j]); j++) code.Append(lines[j]).Append('\n');
                blocks.Add(new Block { Kind = BlockKind.Code, Lang = m.Groups[1].Value, Code = code.ToString().TrimEnd('\n') });
                i = j; para = null; depths.Clear();
                continue;
            }
            if ((m = Markdown.Heading.Match(line)).Success)
            {
                var b = new Block { Kind = BlockKind.Heading, Level = m.Groups[1].Length, Runs = Inl(line[m.Length..].Trim()) };
                string id = Slug(PlainText(b.Runs)), unique = id;
                for (int n = 1; !ids.Add(unique); n++) unique = $"{id}-{n}";
                b.Id = unique;
                blocks.Add(b); para = null; depths.Clear();
                continue;
            }
            if (Rule.IsMatch(line)) { blocks.Add(new Block { Kind = BlockKind.Rule }); para = null; depths.Clear(); continue; }
            if ((m = Quote.Match(line)).Success)
            {
                if (para?.Kind != BlockKind.Quote) blocks.Add(para = new Block { Kind = BlockKind.Quote });
                else para.Runs.Add(new Run("\n", RunStyle.Break));
                para.Runs.AddRange(Inl(line[m.Length..]));
                depths.Clear();
                continue;
            }
            if ((m = Item.Match(line)).Success)
            {
                int indent = m.Groups[1].Value.Replace("\t", "    ").Length;
                while (depths.Count > 0 && indent < depths[^1]) depths.RemoveAt(depths.Count - 1);
                if (depths.Count == 0 || indent > depths[^1]) depths.Add(indent);
                string marker = m.Groups[2].Value;
                bool ordered = char.IsDigit(marker[0]);
                var b = new Block
                {
                    Kind = BlockKind.Item, Level = depths.Count - 1, Ordered = ordered,
                    Number = ordered ? int.Parse(marker[..^1]) : 0,
                    Marker = m.Groups[3].Success ? (m.Groups[3].Value == " " ? "☐" : "☑") : ordered ? marker[..^1] + "." : "•",
                    Runs = Inl(line[m.Length..]),
                };
                blocks.Add(b); para = b;
                continue;
            }
            if (i + 1 < lines.Length && MdTable.IsHeader(line, lines[i + 1]))
            {
                var t = new Block { Kind = BlockKind.Table };
                t.Rows.Add(Cells(line, Inl));
                t.Align = MdTable.Align(lines[i + 1]).Select(a => a == '\0' ? 'l' : a).ToArray();
                int j = i + 2;
                for (; j < lines.Length && !skip[j] && lines[j].Contains('|') && lines[j].Trim().Length > 0; j++)
                    t.Rows.Add(Cells(lines[j], Inl));
                blocks.Add(t);
                i = j - 1; para = null; depths.Clear();
                continue;
            }
            // Строка из одних картинок — отдельный абзац (в PDF картинка встаёт на всю ширину, в HTML — своим блоком).
            // ![[Заметка]] на такой строке — встроенная заметка своим блоком.
            if (ImageLine.IsMatch(line))
            {
                Block? images = null;
                foreach (var e in Markdown.Embeds(line.Trim()))
                {
                    if (e.Groups["wiki"].Success && embed != null && Markdown.IsNoteEmbed(e.Groups["wiki"].Value)
                        && embed(e.Groups["wiki"].Value.Trim()) is { } note)
                    {
                        blocks.Add(note);
                        images = null;
                        continue;
                    }
                    if (images == null) blocks.Add(images = new Block { Kind = BlockKind.Paragraph });
                    images.Runs.AddRange(Inl(e.Value).Where(r => r.Style.HasFlag(RunStyle.Image)));
                }
                para = null; depths.Clear();
                continue;
            }
            // Строка абзаца. Продолжение пункта списка с отступом остаётся в пункте.
            bool continues = para != null && (para.Kind == BlockKind.Paragraph || para.Kind == BlockKind.Item && line.StartsWith(' ') || para.Kind == BlockKind.Item && line.StartsWith('\t'));
            if (!continues) { blocks.Add(para = new Block { Kind = BlockKind.Paragraph }); depths.Clear(); }
            else para!.Runs.Add(new Run("\n", RunStyle.Break));
            para!.Runs.AddRange(Inl(line.Trim()));
        }

        // Сноски — в конце, в порядке первого упоминания (текст сноски может ссылаться на следующую).
        if (ctx.Order.Count > 0)
        {
            var notes = new Block { Kind = BlockKind.Footnotes };
            for (int n = 0; n < ctx.Order.Count; n++)
            {
                var p = new Block { Kind = BlockKind.Paragraph, Id = $"fn-{prefix}{n + 1}", Marker = (n + 1).ToString(), Href = $"#fnref-{prefix}{n + 1}" };
                foreach (var part in ctx.Defs![ctx.Order[n]].Split('\n'))
                {
                    if (p.Runs.Count > 0) p.Runs.Add(new Run("\n", RunStyle.Break));
                    p.Runs.AddRange(Inl(part));
                }
                notes.Children.Add(p);
            }
            blocks.Add(notes);
        }
        return blocks;
    }

    /// <summary>Вынимает определения сносок «[^метка]: текст» (и строки продолжения с отступом); true — строка определения.</summary>
    static bool[] Footnotes(string[] lines, Ctx ctx)
    {
        var skip = new bool[lines.Length];
        bool code = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (Fence.IsMatch(lines[i])) { code = !code; continue; }
            if (code || lines[i].Length < 5 || lines[i][0] != '[') continue;
            var d = Markdown.FootnoteDef.Match(lines[i]);
            if (!d.Success) continue;
            var sb = new StringBuilder(lines[i][d.Length..].Trim());
            skip[i] = true;
            while (i + 1 < lines.Length && lines[i + 1].Trim().Length > 0 && (lines[i + 1].StartsWith("    ") || lines[i + 1].StartsWith('\t')))
            {
                sb.Append('\n').Append(lines[++i].Trim());
                skip[i] = true;
            }
            ctx.Defs ??= new(StringComparer.OrdinalIgnoreCase);
            ctx.Defs.TryAdd(d.Groups[1].Value, sb.ToString());
        }
        return skip;
    }

    static List<List<Run>> Cells(string line, Func<string, List<Run>> inline)
    {
        var row = MdTable.Split(line);
        return row.Cells.Select((_, c) => inline(MdTable.Unescape(row.Text(line, c)))).ToList();
    }

    /// <summary>Оформление внутри строки (сноски без определений — номером-меткой без перехода).</summary>
    public static List<Run> Inlines(string s, Func<string, string?> link, string ext = ".html")
    {
        var runs = new List<Run>();
        Inline1(s, RunStyle.None, null, runs, new Ctx(link, ext, ""));
        return runs;
    }

    static void Inline1(string s, RunStyle style, string? href, List<Run> output, Ctx ctx)
    {
        var link = ctx.Link;
        string ext = ctx.Ext;
        void Plain(string t) { if (t.Length > 0) output.Add(new Run(t, style, href)); }
        var next = new Match?[Inline.Length];
        int pos = 0;
        while (pos < s.Length)
        {
            Match? best = null;
            int kind = -1;
            for (int k = 0; k < Inline.Length; k++)
            {
                if (next[k] == null || next[k]!.Success && next[k]!.Index < pos) next[k] = Inline[k].Match(s, pos);
                var m = next[k]!;
                if (m.Success && (best == null || m.Index < best.Index)) { best = m; kind = k; }
            }
            if (best == null) break;
            Plain(s[pos..best.Index]);
            switch (kind)
            {
                case 0: output.Add(new Run(best.Groups[2].Value, style | RunStyle.Code, href)); break;
                case 1:
                    // ![](путь) — как написан; ![[имя]] — путь к найденному файлу от папки заметки (или имя, если файла нет).
                    if (best.Groups["wiki"].Success)
                    {
                        string name = best.Groups["wiki"].Value.Trim();
                        output.Add(new Run(best.Groups["alt"].Success ? best.Groups["alt"].Value : "", RunStyle.Image, link("!" + name) ?? name));
                    }
                    else output.Add(new Run(best.Groups["alt"].Value, RunStyle.Image, best.Groups["src"].Value));
                    break;
                case 2:
                    string target = best.Groups[1].Value.Trim();
                    string shown = best.Groups[2].Success ? best.Groups[2].Value[1..] : best.Groups[1].Value;
                    string? to = link(target);
                    output.Add(new Run(shown, style | RunStyle.Link | (to == null ? RunStyle.Missing : 0), to));
                    break;
                case 3: Inline1(best.Groups[1].Value, style | RunStyle.Link, LocalHref(best.Groups[2].Value, ext), output, ctx); break;
                case 4: output.Add(new Run(best.Value, style | RunStyle.Link, best.Value)); break;
                case 11: output.Add(new Run(best.Value, style | RunStyle.Tag, href)); break;
                case 12:
                {
                    string label = best.Groups[1].Value;
                    if (ctx.Defs == null) { output.Add(new Run(label, style | RunStyle.Note)); break; }
                    if (!ctx.Defs.ContainsKey(label)) { Plain(best.Value); break; } // сноски нет — как написано
                    bool first = !ctx.Numbers.TryGetValue(label, out int n);
                    if (first) { ctx.Order.Add(label); ctx.Numbers[label] = n = ctx.Order.Count; }
                    output.Add(new Run(n.ToString(), style | RunStyle.Note, $"#fn-{ctx.Prefix}{n}", first ? $"fnref-{ctx.Prefix}{n}" : null));
                    break;
                }
                default: Inline1(best.Groups[kind <= 6 ? 2 : 1].Value, style | Wraps[kind - 5], href, output, ctx); break;
            }
            pos = best.Index + best.Length;
        }
        if (pos < s.Length) Plain(s[pos..]);
    }

    /// <summary>Ссылка Markdown на соседнюю заметку «Имя.md» ведёт на её файл экспорта.</summary>
    static string LocalHref(string url, string ext)
    {
        if (url.Contains("://") || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return url;
        int hash = url.IndexOf('#');
        string path = hash < 0 ? url : url[..hash], frag = hash < 0 ? "" : url[hash..];
        return path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? path[..^3] + ext + frag : url;
    }

    public static string PlainText(IEnumerable<Run> runs) => string.Concat(runs.Select(r => r.Style.HasFlag(RunStyle.Break) ? " " : r.Text));

    /// <summary>Якорь заголовка: «Задачи и сроки» → «задачи-и-сроки».</summary>
    public static string Slug(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_') sb.Append(c);
            else if (char.IsWhiteSpace(c) && sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().TrimEnd('-');
    }

    // ---------- ссылки между заметками ----------

    /// <summary>
    /// Адрес [[ссылки]] из заметки <paramref name="from"/>: относительный путь к файлу экспорта цели (+ #якорь) или null.
    /// <paramref name="page"/> — заметка, в файл которой идёт экспорт (у встроенной заметки адреса считаются от него).
    /// </summary>
    public static Func<string, string?> Linker(Vault? vault, Note from, string ext, Note? page = null) => target =>
    {
        page ??= from;
        bool ascii = ext == ".pdf";
        if (target.StartsWith('!')) // ![[вложение]]
            return vault?.ResolveFile(target[1..], from, wiki: true) is { } file
                ? RelativeHref(page.Folder, Path.GetRelativePath(vault.Root, file).Replace('\\', '/'), ascii) : null;
        int hash = target.IndexOf('#');
        string anchor = hash < 0 ? "" : "#" + Slug(target[(hash + 1)..]);
        if (anchor == "#") anchor = "";
        var note = hash == 0 ? from : vault?.Resolve(target, from);
        if (note == null) return null;
        if (note == page) return anchor.Length > 0 ? anchor : "#";
        return RelativeHref(page.Folder, note.Rel[..^3] + ext, ascii) + anchor;
    };

    /// <summary>
    /// Встраивание ![[Имя]] / ![[Имя#раздел]] из заметки <paramref name="from"/>: блок с разобранной заметкой (или её разделом).
    /// null — цель не заметка (файл). Цепочка <paramref name="chain"/> защищает от кругов: заметка, уже открытая выше, не встраивается.
    /// </summary>
    public static Func<string, Block?> Embedder(Vault? vault, Note from, string ext, Note? page = null, HashSet<string>? chain = null, int depth = 0) => target =>
    {
        if (vault == null || !Markdown.IsNoteEmbed(target)) return null;
        page ??= from;
        int hash = target.IndexOf('#');
        string name = (hash < 0 ? target : target[..hash]).Trim(), section = hash < 0 ? "" : target[(hash + 1)..].Trim();
        var note = name.Length == 0 ? from : vault.Resolve(name, from);
        var block = new Block { Kind = BlockKind.Embed, Title = (note?.Title ?? name) + (section.Length > 0 ? " › " + section : "") };
        if (note == null) { block.Message = Loc.T("exp.nonote", name); return block; }
        block.Href = Linker(vault, from, ext, page)(name.Length == 0 ? "#" + section : target);
        block.BaseDir = Path.GetDirectoryName(note.Path);
        chain ??= new(StringComparer.OrdinalIgnoreCase) { from.Path };
        if ((chain.Contains(note.Path) && section.Length == 0) || depth >= MaxEmbedDepth)
        {
            block.Message = depth >= MaxEmbedDepth ? Loc.T("exp.deep") : Loc.T("exp.circular", note.Title);
            return block;
        }
        string? text = section.Length > 0 ? Markdown.Section(note.Text, section) : note.Text;
        if (text == null) { block.Message = Loc.T("exp.nosection", section, note.Title); return block; }
        var inner = new HashSet<string>(chain, StringComparer.OrdinalIgnoreCase) { note.Path };
        block.Children = Parse(text, Linker(vault, note, ext, page), ext, Embedder(vault, note, ext, page, inner, depth + 1), $"{Slug(note.Title)}-{depth + 1}-");
        return block;
    };

    /// <summary>
    /// Путь от папки <paramref name="fromFolder"/> к <paramref name="rel"/> (оба от корня через '/'), части экранированы для адреса.
    /// В HTML кириллица остаётся как есть (браузеры её понимают), в PDF адрес должен быть ASCII — <paramref name="ascii"/>.
    /// </summary>
    public static string RelativeHref(string fromFolder, string rel, bool ascii = false)
    {
        var from = fromFolder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var to = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int common = 0;
        while (common < from.Length && common < to.Length - 1 && from[common].Equals(to[common], StringComparison.OrdinalIgnoreCase)) common++;
        var parts = Enumerable.Repeat("..", from.Length - common).Concat(to.Skip(common).Select(p => ascii ? Uri.EscapeDataString(p) : EscapePart(p)));
        return string.Join('/', parts);
    }

    static string EscapePart(string s) => s.Replace("%", "%25").Replace(" ", "%20").Replace("#", "%23").Replace("?", "%3F");

    // ---------- HTML ----------

    /// <summary>Готовая страница HTML заметки в цветах и шрифтах темы. Картинки рядом с заметкой встраиваются в страницу.</summary>
    public static string Html(Vault? vault, Note note, string text, ExportStyle st)
    {
        var blocks = Parse(text, Linker(vault, note, ".html"), ".html", Embedder(vault, note, ".html"));
        return Html(note.Title, blocks, st, Path.GetDirectoryName(note.Path));
    }

    public static string Html(string title, List<Block> blocks, ExportStyle st, string? baseDir = null)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html lang=\"" + Loc.Lang + "\">\n<head>\n<meta charset=\"utf-8\">\n")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
          .Append("<meta name=\"generator\" content=\"Leaflet\">\n")
          .Append("<title>").Append(E(title)).Append("</title>\n<style>\n").Append(Css(st)).Append("</style>\n</head>\n<body>\n<main>\n");
        Body(sb, blocks, baseDir, baseDir, nested: false);
        sb.Append("</main>\n</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>Блоки страницы; <paramref name="nested"/> — внутри встроенной заметки (у заголовков нет якорей: они есть у страницы).</summary>
    static void Body(StringBuilder sb, List<Block> blocks, string? baseDir, string? pageDir, bool nested)
    {
        void Runs(StringBuilder sb, List<Run> runs, string? baseDir) => Export.Runs(sb, runs, baseDir, pageDir);
        var lists = new Stack<(int Depth, bool Ordered)>();
        void CloseLists(int depth)
        {
            while (lists.Count > 0 && lists.Peek().Depth > depth)
                sb.Append("</li>\n</").Append(lists.Pop().Ordered ? "ol" : "ul").Append(">\n");
        }

        foreach (var b in blocks)
        {
            if (b.Kind != BlockKind.Item) CloseLists(-1);
            switch (b.Kind)
            {
                case BlockKind.Heading:
                    sb.Append(nested ? $"<h{b.Level}>" : $"<h{b.Level} id=\"{E(b.Id)}\">");
                    Runs(sb, b.Runs, baseDir);
                    sb.Append($"</h{b.Level}>\n");
                    break;
                case BlockKind.Paragraph:
                    sb.Append("<p>");
                    Runs(sb, b.Runs, baseDir);
                    sb.Append("</p>\n");
                    break;
                case BlockKind.Quote:
                    sb.Append("<blockquote><p>");
                    Runs(sb, b.Runs, baseDir);
                    sb.Append("</p></blockquote>\n");
                    break;
                case BlockKind.Code:
                    sb.Append("<pre><code").Append(b.Lang.Length > 0 ? $" class=\"language-{E(b.Lang)}\"" : "").Append('>')
                      .Append(E(b.Code)).Append("</code></pre>\n");
                    break;
                case BlockKind.Rule:
                    sb.Append("<hr>\n");
                    break;
                case BlockKind.Table:
                    sb.Append("<table>\n");
                    for (int r = 0; r < b.Rows.Count; r++)
                    {
                        string cell = r == 0 ? "th" : "td";
                        sb.Append("<tr>");
                        for (int c = 0; c < b.Rows[r].Count; c++)
                        {
                            char a = c < b.Align.Length ? b.Align[c] : 'l';
                            sb.Append('<').Append(cell).Append(a == 'c' ? " style=\"text-align:center\"" : a == 'r' ? " style=\"text-align:right\"" : "").Append('>');
                            Runs(sb, b.Rows[r][c], baseDir);
                            sb.Append("</").Append(cell).Append('>');
                        }
                        sb.Append("</tr>\n");
                    }
                    sb.Append("</table>\n");
                    break;
                case BlockKind.Item:
                    CloseLists(b.Level);
                    if (lists.Count > 0 && lists.Peek().Depth == b.Level && lists.Peek().Ordered != b.Ordered) CloseLists(b.Level - 1);
                    if (lists.Count == 0 || lists.Peek().Depth < b.Level)
                    {
                        sb.Append(b.Ordered ? (b.Number != 1 ? $"<ol start=\"{b.Number}\">\n" : "<ol>\n") : "<ul>\n");
                        lists.Push((b.Level, b.Ordered));
                    }
                    else sb.Append("</li>\n");
                    bool task = b.Marker is "☐" or "☑";
                    sb.Append(task ? $"<li class=\"task{(b.Marker == "☑" ? " done" : "")}\"><span class=\"box\">{b.Marker}</span>" : "<li>");
                    Runs(sb, b.Runs, baseDir);
                    break;
                case BlockKind.Embed:
                    sb.Append("<div class=\"embed\">\n<div class=\"embed-title\">")
                      .Append(b.Href != null ? $"<a href=\"{E(b.Href)}\">{E(b.Title)}</a>" : E(b.Title)).Append("</div>\n");
                    if (b.Message.Length > 0) sb.Append("<p class=\"embed-missing\">").Append(E(b.Message)).Append("</p>\n");
                    else Body(sb, b.Children, b.BaseDir, pageDir, nested: true);
                    sb.Append("</div>\n");
                    break;
                case BlockKind.Footnotes:
                    sb.Append("<section class=\"footnotes\">\n<ol>\n");
                    foreach (var n in b.Children)
                    {
                        sb.Append($"<li id=\"{E(n.Id)}\">");
                        Runs(sb, n.Runs, baseDir);
                        sb.Append($" <a class=\"back\" href=\"{E(n.Href!)}\" title=\"{E(Loc.T("exp.backtotext"))}\">↩</a></li>\n");
                    }
                    sb.Append("</ol>\n</section>\n");
                    break;
            }
        }
        CloseLists(-1);
    }

    static void Runs(StringBuilder sb, List<Run> runs, string? baseDir, string? pageDir)
    {
        foreach (var r in runs)
        {
            var s = r.Style;
            if (s.HasFlag(RunStyle.Break)) { sb.Append("<br>\n"); continue; }
            if (s.HasFlag(RunStyle.Image)) { sb.Append($"<img src=\"{E(ImageSrc(r.Href!, baseDir, pageDir))}\" alt=\"{E(r.Text)}\">"); continue; }
            if (s.HasFlag(RunStyle.Note))
            {
                sb.Append("<sup class=\"fn\">").Append(r.Href == null ? E(r.Text)
                    : $"<a{(r.Id != null ? $" id=\"{E(r.Id)}\"" : "")} href=\"{E(r.Href)}\">{E(r.Text)}</a>").Append("</sup>");
                continue;
            }
            var close = new Stack<string>();
            void Open(string tag, string attrs = "") { sb.Append('<').Append(tag).Append(attrs).Append('>'); close.Push(tag); }
            if (s.HasFlag(RunStyle.Missing)) Open("span", $" class=\"missing\" title=\"{E(Loc.T("exp.missing"))}\"");
            else if (s.HasFlag(RunStyle.Link) && r.Href != null) Open("a", $" href=\"{E(r.Href)}\"");
            if (s.HasFlag(RunStyle.Bold)) Open("strong");
            if (s.HasFlag(RunStyle.Italic)) Open("em");
            if (s.HasFlag(RunStyle.Strike)) Open("del");
            if (s.HasFlag(RunStyle.Mark)) Open("mark");
            if (s.HasFlag(RunStyle.Tag)) Open("span", " class=\"tag\"");
            if (s.HasFlag(RunStyle.Code)) Open("code");
            sb.Append(E(r.Text));
            while (close.Count > 0) sb.Append("</").Append(close.Pop()).Append('>');
        }
    }

    /// <summary>Картинка рядом с заметкой встраивается (data:), чтобы страница не зависела от папки хранилища.</summary>
    static string ImageSrc(string src, string? baseDir, string? pageDir)
    {
        // У встроенной заметки ![](путь) — от её папки, ![[файл]] — уже от папки страницы.
        if ((ImagePath(src, baseDir) ?? ImagePath(src, pageDir)) is not { } path) return src;
        string mime = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".svg" => "image/svg+xml", _ => "",
        };
        if (mime.Length == 0 || new FileInfo(path).Length > 10_000_000) return src;
        return $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
    }

    public static string? ImagePath(string src, string? baseDir)
    {
        if (baseDir == null || src.Contains("://")) return null;
        try
        {
            string path = Path.GetFullPath(Path.Combine(baseDir, Uri.UnescapeDataString(src)));
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    static string E(string s) => WebUtility.HtmlEncode(s);

    public static string Css(uint argb) => (argb >> 24) == 0xff
        ? $"#{argb & 0xffffff:x6}"
        : $"rgba({(argb >> 16) & 0xff},{(argb >> 8) & 0xff},{argb & 0xff},{((argb >> 24) / 255.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)})";

    static string Px(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "px";

    static string Fonts(string[] names, string generic) => string.Join(", ", names.Select(n => $"\"{n}\"").Append(generic));

    static string Css(ExportStyle st)
    {
        bool mono = st.Fonts.Length > 0 && st.Fonts[0] is "Consolas" or "Cascadia Mono" or "Courier New";
        var sb = new StringBuilder();
        sb.Append($$"""
            :root { color-scheme: dark; }
            html { background: {{Css(st.Bg)}}; -webkit-print-color-adjust: exact; print-color-adjust: exact; }
            body { margin: 0; background: {{Css(st.Bg)}}; color: {{Css(st.Fg)}}; font: {{Px(st.Size)}}/1.6 {{Fonts(st.Fonts, mono ? "monospace" : "sans-serif")}}; }
            main { max-width: {{Px(st.Width > 0 ? st.Width : 820)}}; margin: 0 auto; padding: 48px 36px 72px; overflow-wrap: break-word; }
            h1, h2, h3, h4, h5, h6 { color: {{Css(st.Heading)}}; font-weight: 700; line-height: 1.3; margin: 1.3em 0 .5em; }
            main > :first-child { margin-top: 0; }
            p { margin: 0 0 .85em; }
            a { color: {{Css(st.Accent)}}; text-decoration: underline; text-underline-offset: 2px; }
            .missing { color: {{Css(st.Mute)}}; text-decoration: underline dotted; cursor: help; }
            .tag { color: {{Css(st.Accent)}}; }
            mark { background: {{Css(st.Highlight)}}; color: inherit; }
            del { color: {{Css(st.Mute)}}; }
            code, pre { font-family: {{Fonts(st.MonoFonts, "monospace")}}; font-size: {{Px(st.MonoSize)}}; }
            code { background: {{Css(st.CodeBack)}}; padding: .1em .3em; border-radius: 3px; }
            pre { background: {{Css(st.CodeBack)}}; padding: 12px 16px; border-radius: 6px; overflow-x: auto; line-height: 1.45; margin: 0 0 1em; }
            pre code { background: none; padding: 0; }
            blockquote { margin: 0 0 1em; padding: 2px 0 2px 14px; border-left: 3px solid {{Css(st.Accent)}}; color: {{Css(st.QuoteText)}}; font-style: italic; }
            blockquote p { margin: 0; }
            hr { border: 0; border-top: 1px solid {{Css(st.Line)}}; margin: 1.6em 0; }
            ul, ol { margin: 0 0 .85em; padding-left: 1.6em; }
            li > ul, li > ol { margin: 0; }
            li::marker { color: {{Css(st.Mute)}}; }
            li.task { list-style: none; }
            li.task .box { display: inline-block; width: 1.4em; margin-left: -1.4em; font-family: "Segoe UI Symbol", {{Fonts(st.Fonts, "sans-serif")}}; color: {{Css(st.Mute)}}; }
            li.done .box { color: {{Css(st.Accent)}}; }
            table { border-collapse: collapse; margin: 0 0 1em; }
            th, td { border: 1px solid {{Css(st.Line)}}; padding: 6px 12px; vertical-align: top; }
            th { background: {{Css(st.Panel)}}; color: {{Css(st.Heading)}}; text-align: left; }
            img { max-width: 100%; }
            .embed { margin: 0 0 1em; padding: 2px 0 2px 14px; border-left: 3px solid {{Css(st.Line)}}; }
            .embed-title { color: {{Css(st.Mute)}}; font-size: .85em; margin-bottom: .35em; }
            .embed-title a { color: inherit; text-decoration: none; }
            .embed-missing { color: {{Css(st.Mute)}}; font-style: italic; }
            sup.fn { font-size: .72em; line-height: 0; }
            sup.fn a { text-decoration: none; }
            .footnotes { margin-top: 2em; padding-top: .6em; border-top: 1px solid {{Css(st.Line)}}; font-size: .9em; }
            .footnotes ol { padding-left: 1.6em; }
            .footnotes a.back { text-decoration: none; color: {{Css(st.Mute)}}; }

            """);
        for (int i = 1; i <= 6; i++) sb.Append($"h{i} {{ font-size: {Px(st.HeadingSizes[i])}; }}\n");
        if (st.HeadingMarks)
            for (int i = 1; i <= 6; i++) sb.Append($"h{i}::before {{ content: \"{new string('#', i)} \"; color: {Css(st.Mute)}; font-weight: 400; }}\n");
        return sb.ToString();
    }

    // ---------- файлы ----------

    /// <summary>Пишет заметку в файл: .pdf — PDF, иначе HTML.</summary>
    public static void ToFile(Vault? vault, Note note, string text, string path, ExportStyle st)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null) Directory.CreateDirectory(dir);
        if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var blocks = Parse(text, Linker(vault, note, ".pdf"), ".pdf", Embedder(vault, note, ".pdf"));
            using var file = File.Create(path);
            PdfExport.Write(file, note.Title, blocks, st, Path.GetDirectoryName(note.Path));
        }
        else File.WriteAllText(path, Html(vault, note, text, st), new UTF8Encoding(false));
    }

    /// <summary>Все заметки хранилища в папку <paramref name="dir"/> с той же раскладкой по папкам; ссылки между ними работают.</summary>
    public static int AllToFolder(Vault vault, string dir, string ext, ExportStyle st)
    {
        int n = 0;
        foreach (var note in vault.Notes.ToList())
        {
            ToFile(vault, note, note.Text, Path.Combine(dir, note.Rel[..^3].Replace('/', Path.DirectorySeparatorChar) + ext), st);
            n++;
        }
        return n;
    }
}
