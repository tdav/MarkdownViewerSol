using System.Text;
using System.Text.RegularExpressions;

namespace Leaflet.Core;

/// <summary>
/// Вложения: картинки и другие файлы рядом с заметками. Ссылки на них — обычные пути Markdown ![](assets/x.png)
/// (читаются любым редактором и браузером) или ![[x.png]] в духе Obsidian (по имени где угодно в хранилище).
/// </summary>
public sealed partial class Vault
{
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];
    public static bool IsImage(string path) => ImageExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Любая ссылка Markdown с путём: ![](…) и [текст](…), путь в группе src (в &lt;…&gt; — с пробелами).</summary>
    static readonly Regex PathLink = new(@"(?<img>!)?\[(?<alt>[^\[\]\n]*)\]\((?:<(?<src>[^<>\n]+)>|(?<src>[^()\s]+))\)", RegexOptions.Compiled);

    Dictionary<string, List<string>>? _files; // имя файла (нижний регистр) → полные пути; кроме .md

    /// <summary>
    /// Папка для новых вложений: просто имя («assets») — рядом с заметкой, с «/» в начале («/assets») — от корня хранилища.
    /// </summary>
    public string AttachFolder { get; set; } = "assets";

    public string AttachDir(Note note)
    {
        string name = AttachFolder.Replace('\\', '/').Trim();
        bool fromRoot = name.StartsWith('/');
        var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Clean).Where(p => p.Length > 0 && p != "..");
        string baseDir = fromRoot ? Root : System.IO.Path.GetDirectoryName(note.Path)!;
        return System.IO.Path.Combine([baseDir, .. parts]);
    }

    /// <summary>
    /// Файл по ссылке из заметки или null. ![](путь) — от папки заметки («/путь» — от корня).
    /// ![[имя]] — от корня, рядом с заметкой, в папке вложений, потом по имени в любой папке (ближе к заметке — раньше).
    /// </summary>
    public string? ResolveFile(string src, Note from, bool wiki)
    {
        if (src.Length == 0 || src.Contains("://") || src.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return null;
        string dir = System.IO.Path.GetDirectoryName(from.Path)!;
        try
        {
            if (!wiki)
            {
                string full = src.StartsWith('/') ? System.IO.Path.Combine(Root, src.TrimStart('/')) : System.IO.Path.Combine(dir, src);
                full = System.IO.Path.GetFullPath(full);
                return File.Exists(full) ? full : null;
            }
            src = src.Replace('\\', '/').Trim();
            if (!System.IO.Path.HasExtension(src) || src.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return null; // это заметка
            foreach (var baseDir in new[] { Root, dir, AttachDir(from) })
            {
                string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, src.TrimStart('/')));
                if (File.Exists(full)) return full;
            }
            if (!Files().TryGetValue(System.IO.Path.GetFileName(src).ToLowerInvariant(), out var list)) return null;
            string tail = "/" + src.ToLowerInvariant();
            return list.Where(p => src.IndexOf('/') < 0 || p.Replace('\\', '/').ToLowerInvariant().EndsWith(tail, StringComparison.Ordinal))
                .OrderBy(p => p.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(p => p.Count(c => c == '\\')).FirstOrDefault();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    Dictionary<string, List<string>> Files()
    {
        if (_files != null) return _files;
        var map = new Dictionary<string, List<string>>();
        void Walk(string d)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(d))
                {
                    if (f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".leaflet-tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    string key = System.IO.Path.GetFileName(f).ToLowerInvariant();
                    if (!map.TryGetValue(key, out var list)) map[key] = list = [];
                    list.Add(f);
                }
                foreach (var sub in Directory.EnumerateDirectories(d))
                    if (!System.IO.Path.GetFileName(sub).StartsWith('.')) Walk(sub);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Walk(Root);
        return _files = map;
    }

    /// <summary>Путь для ссылки из заметки на файл: относительный, через «/»; пробел, %, #, ?, скобки экранированы.</summary>
    public static string LinkPath(Note from, string file)
    {
        string rel = System.IO.Path.GetRelativePath(System.IO.Path.GetDirectoryName(from.Path)!, file).Replace('\\', '/');
        return string.Join('/', rel.Split('/').Select(EscapePart));
    }

    static string EscapePart(string s) =>
        s.Replace("%", "%25").Replace(" ", "%20").Replace("#", "%23").Replace("?", "%3F").Replace("(", "%28").Replace(")", "%29");

    public bool InVault(string path) =>
        System.IO.Path.GetFullPath(path).StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Кладёт файл в папку вложений заметки; занятое имя получает номер («x 2.png»).
    /// Файл, который уже лежит в хранилище, не копируется. Возвращает полный путь вложения.
    /// </summary>
    public string AddAttachment(Note note, string source)
    {
        source = System.IO.Path.GetFullPath(source);
        if (InVault(source)) return source;
        return Store(note, System.IO.Path.GetFileName(source), path => File.Copy(source, path));
    }

    public string AddAttachment(Note note, byte[] data, string name) => Store(note, name, path => File.WriteAllBytes(path, data));

    string Store(Note note, string name, Action<string> write)
    {
        if (ReadOnly) throw new IOException(Loc.T("attach.readonly"));
        string dir = AttachDir(note);
        string clean = Clean(name.Replace('/', '-').Replace('\\', '-'));
        if (clean.Length == 0) clean = Loc.T("attach.stem");
        string stem = System.IO.Path.GetFileNameWithoutExtension(clean), ext = System.IO.Path.GetExtension(clean);
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, clean);
        for (int i = 2; File.Exists(path); i++) path = System.IO.Path.Combine(dir, $"{stem} {i}{ext}");
        write(path);
        _files = null;
        return path;
    }

    /// <summary>
    /// Переименовывает вложение (в той же папке) и правит ссылки на него во всех заметках:
    /// пути ![](…)/[](…) и ![[…]]/[[…]] (по имени, если оно однозначно, иначе путь от корня). Возвращает изменённые заметки.
    /// </summary>
    public (string Path, List<Note> Changed) MoveAttachment(string file, string newName)
    {
        file = System.IO.Path.GetFullPath(file);
        newName = Clean(newName.Replace('/', '-').Replace('\\', '-'));
        if (newName.Length == 0) throw new IOException(Loc.T("name.empty"));
        if (!System.IO.Path.HasExtension(newName)) newName += System.IO.Path.GetExtension(file);
        string target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file)!, newName);
        if (target == file) return (file, []);
        if (!target.Equals(file, StringComparison.OrdinalIgnoreCase) && File.Exists(target))
            throw new IOException(Loc.T("attach.exists", newName));

        // Ссылки ищем до переименования: по имени они находятся только пока файл на месте.
        var wikiRefs = new Dictionary<Note, List<Group>>();
        foreach (var n in _byPath.Values)
        {
            if (!n.Text.Contains("[[")) continue;
            var groups = Markdown.WikiLink.Matches(n.Text).Select(m => m.Groups[1])
                .Where(g => ResolveFile(g.Value, n, wiki: true) is { } p && p.Equals(file, StringComparison.OrdinalIgnoreCase)).ToList();
            if (groups.Count > 0) wikiRefs[n] = groups;
        }

        if (!ReadOnly) File.Move(file, target);
        _files = null;
        string fromRoot = System.IO.Path.GetRelativePath(Root, target).Replace('\\', '/');
        string byName = Files().TryGetValue(newName.ToLowerInvariant(), out var same) && same.Count == 1 ? newName : fromRoot;

        var changed = new List<Note>();
        foreach (var n in _byPath.Values.ToList())
        {
            string text = n.Text;
            if (wikiRefs.TryGetValue(n, out var groups))
            {
                var sb = new StringBuilder(text.Length + 32);
                int pos = 0;
                foreach (var g in groups)
                {
                    sb.Append(text, pos, g.Index - pos).Append(g.Value.Contains('/') ? fromRoot : byName);
                    pos = g.Index + g.Length;
                }
                text = sb.Append(text, pos, text.Length - pos).ToString();
            }
            string dir = System.IO.Path.GetDirectoryName(n.Path)!;
            text = Rebase(text, dir, dir, full => full.Equals(file, StringComparison.OrdinalIgnoreCase) ? target : null);
            if (text == n.Text) continue;
            Save(n, text);
            changed.Add(n);
        }
        return (target, changed);
    }

    /// <summary>
    /// Переписывает относительные пути ![](…) и [](…) текста, записанного в папке <paramref name="oldDir"/>, для папки <paramref name="newDir"/>.
    /// <paramref name="map"/> получает полный путь цели и возвращает новый (или null — не трогать). Адреса, «/от корня», #якоря и код не трогаются.
    /// </summary>
    string Rebase(string text, string oldDir, string newDir, Func<string, string?> map)
    {
        if (!text.Contains("](")) return text;
        List<(int Start, int End)>? code = null;
        var sb = new StringBuilder(text.Length + 32);
        int pos = 0;
        foreach (Match m in PathLink.Matches(text))
        {
            var g = m.Groups["src"];
            string raw = g.Value;
            if (raw.Contains("://") || raw.StartsWith('#') || raw.StartsWith('/') || raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
            code ??= Markdown.CodeRanges(text);
            if (code.Any(c => m.Index >= c.Start && m.Index < c.End)) continue;
            int lineStart = text.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1, lineEnd = text.IndexOf('\n', m.Index);
            string line = text[lineStart..(lineEnd < 0 ? text.Length : lineEnd)];
            if (Markdown.CodeSpan.Matches(line).Any(c => m.Index - lineStart >= c.Index && m.Index - lineStart < c.Index + c.Length)) continue;

            bool angle = text[g.Index - 1] == '<';
            int hash = m.Groups["img"].Success ? -1 : raw.IndexOf('#');
            string pathPart = hash < 0 ? raw : raw[..hash], frag = hash < 0 ? "" : raw[hash..];
            string decoded = angle ? pathPart : Markdown.Unescape(pathPart);
            string full, target;
            try
            {
                full = System.IO.Path.GetFullPath(System.IO.Path.Combine(oldDir, decoded));
                if (map(full) is not { } t) continue;
                target = t;
                if (System.IO.Path.GetFullPath(System.IO.Path.Combine(newDir, decoded)).Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { continue; }
            string rel = System.IO.Path.GetRelativePath(newDir, target).Replace('\\', '/');
            string written = angle ? rel : string.Join('/', rel.Split('/').Select(EscapePart));
            sb.Append(text, pos, g.Index - pos).Append(written).Append(frag);
            pos = g.Index + g.Length;
        }
        if (pos == 0) return text;
        return sb.Append(text, pos, text.Length - pos).ToString();
    }
}
