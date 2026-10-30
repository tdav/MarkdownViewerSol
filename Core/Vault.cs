using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Leaflet.Core;

/// <summary>Заметка — файл .md. Текст держим в памяти: по нему ищем и строим обратные ссылки.</summary>
public sealed class Note
{
    public string Path = "";
    public string Rel = "";      // путь от корня через '/', с .md
    public string Title = "";    // имя файла без .md
    public string Folder = "";   // папка от корня через '/', "" — корень
    public string Text = "";     // последнее сохранённое (или прочитанное) содержимое
    public string[] Links = [];  // ключи [[ссылок]], см. Markdown.LinkKey
    public string[] Tags = [];   // теги без «#», см. Markdown.TagKeys
    public DateTime Modified;

    public string TitleKey => Title.ToLowerInvariant();
    public string RelKey => Rel[..^3].ToLowerInvariant();
}

public readonly record struct Backlink(Note From, string Snippet, int MatchStart, int MatchLength, int Offset);
public readonly record struct SearchHit(Note Note, int Offset, int Line, string Snippet, int MatchStart);
public sealed record ExternalChanges(List<Note> Changed, bool ListChanged, List<Note> Removed, List<(Note Note, string OldRel)> Renamed);

/// <summary>Хранилище — папка с .md. Источник правды только файлы, индекс собирается в памяти.</summary>
public sealed partial class Vault
{
    static readonly UTF8Encoding Utf8 = new(false);
    readonly Dictionary<string, Note> _byPath = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, List<Note>>? _byTitle;
    FileSystemWatcher? _watcher;
    Timer? _watchTimer;
    readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);

    public string Root { get; }
    public string Name => System.IO.Path.GetFileName(Root.TrimEnd('\\', '/'));
    public bool ReadOnly { get; init; }
    public bool Loaded { get; private set; }
    public IReadOnlyCollection<Note> Notes => _byPath.Values;

    public Vault(string root) => Root = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/');

    // ---- загрузка ----

    /// <summary>Читает все заметки папки (вызывать в фоне).</summary>
    public List<Note> Scan()
    {
        var files = new List<string>();
        Collect(Root, files);
        var notes = new Note?[files.Count];
        Parallel.For(0, files.Count, i => notes[i] = Read(files[i]));
        return notes.Where(n => n != null).ToList()!;
    }

    static void Collect(string dir, List<string> files)
    {
        try
        {
            files.AddRange(Directory.EnumerateFiles(dir, "*.md"));
            foreach (var sub in Directory.EnumerateDirectories(dir))
                if (!System.IO.Path.GetFileName(sub).StartsWith('.')) Collect(sub, files); // .obsidian, .git, .trash
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public Note? Read(string path)
    {
        try
        {
            var note = Describe(path);
            note.Text = File.ReadAllText(path);
            note.Links = Markdown.LinkKeys(note.Text);
            note.Tags = Markdown.TagKeys(note.Text);
            note.Modified = File.GetLastWriteTime(path);
            return note;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    Note Describe(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        string rel = System.IO.Path.GetRelativePath(Root, path).Replace('\\', '/');
        int slash = rel.LastIndexOf('/');
        return new Note
        {
            Path = path,
            Rel = rel,
            Title = System.IO.Path.GetFileNameWithoutExtension(path),
            Folder = slash < 0 ? "" : rel[..slash],
        };
    }

    /// <summary>Подменяет индекс результатом <see cref="Scan"/>, сохраняя уже открытые объекты заметок.</summary>
    public void SetAll(List<Note> notes)
    {
        var old = new Dictionary<string, Note>(_byPath, StringComparer.OrdinalIgnoreCase);
        _byPath.Clear();
        foreach (var n in notes)
        {
            if (old.TryGetValue(n.Path, out var kept))
            {
                kept.Text = n.Text; kept.Links = n.Links; kept.Tags = n.Tags; kept.Modified = n.Modified;
                _byPath[n.Path] = kept;
            }
            else _byPath[n.Path] = n;
        }
        _byTitle = null;
        Loaded = true;
    }

    public Note? Get(string path) => _byPath.GetValueOrDefault(System.IO.Path.GetFullPath(path));

    /// <summary>Добавляет одну заметку до полной загрузки (файл из командной строки).</summary>
    public Note? Add(string path)
    {
        var existing = Get(path);
        if (existing != null) return existing;
        var note = Read(path);
        if (note == null) return null;
        _byPath[note.Path] = note;
        _byTitle = null;
        return note;
    }

    // ---- ссылки ----

    Dictionary<string, List<Note>> ByTitle()
    {
        if (_byTitle != null) return _byTitle;
        var map = new Dictionary<string, List<Note>>();
        foreach (var n in _byPath.Values)
        {
            if (!map.TryGetValue(n.TitleKey, out var list)) map[n.TitleKey] = list = [];
            list.Add(n);
        }
        return _byTitle = map;
    }

    /// <summary>Заметка по ссылке: по имени (сначала в той же папке, потом ближе к корню) или по пути.</summary>
    public Note? Resolve(string target, Note? from = null) => ResolveKey(Markdown.LinkKey(target), from);

    Note? ResolveKey(string key, Note? from)
    {
        if (key.Length == 0) return from;
        if (key.Contains('/'))
        {
            foreach (var n in _byPath.Values)
                if (n.RelKey == key || n.RelKey.EndsWith("/" + key, StringComparison.Ordinal)) return n;
            return null;
        }
        if (!ByTitle().TryGetValue(key, out var list)) return null;
        if (list.Count == 1) return list[0];
        return list.FirstOrDefault(n => from != null && n.Folder == from.Folder)
            ?? list.OrderBy(n => n.Rel.Count(c => c == '/')).ThenBy(n => n.Rel.Length).First();
    }

    public List<Backlink> Backlinks(Note target)
    {
        var result = new List<Backlink>();
        foreach (var n in _byPath.Values)
        {
            if (n == target || n.Links.Length == 0) continue;
            bool links = false;
            foreach (var key in n.Links)
                if (ResolveKey(key, n) == target) { links = true; break; }
            if (!links) continue;
            foreach (System.Text.RegularExpressions.Match m in Markdown.WikiLink.Matches(n.Text))
            {
                if (ResolveKey(Markdown.LinkKey(m.Groups[1].Value), n) != target) continue;
                var (line, start) = Markdown.LineAround(n.Text, m.Index, m.Length);
                var (snippet, s, len) = Markdown.Plain(line, start, Math.Min(m.Length, line.Length - start));
                result.Add(new Backlink(n, snippet, s, len, m.Index));
                break;
            }
        }
        result.Sort((a, b) => string.Compare(a.From.Title, b.From.Title, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    // ---- поиск ----

    /// <summary>Поиск без учёта регистра по тексту всех заметок (можно звать в фоне по снимку).</summary>
    public static List<SearchHit> SearchText(Note[] notes, string query, int perNote, int max, CancellationToken ct)
    {
        var hits = new List<SearchHit>();
        foreach (var n in notes)
        {
            if (ct.IsCancellationRequested || hits.Count >= max) break;
            string text = n.Text;
            int idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase), found = 0, line = 1, counted = 0;
            while (idx >= 0 && found < perNote)
            {
                for (; counted < idx; counted++) if (text[counted] == '\n') line++;
                var (snippet, start) = Markdown.LineAround(text, idx, query.Length);
                hits.Add(new SearchHit(n, idx, line, snippet, start));
                found++;
                int next = text.IndexOf('\n', idx);
                idx = next < 0 ? -1 : text.IndexOf(query, next, StringComparison.OrdinalIgnoreCase);
            }
        }
        return hits;
    }

    // ---- теги ----

    /// <summary>Тег (без «#») есть в заметке: точно такой или вложенный («проект» находит «проект/сайт»).</summary>
    public static bool HasTag(Note note, string tag)
    {
        foreach (var t in note.Tags)
            if (t.Equals(tag, StringComparison.OrdinalIgnoreCase) || t.StartsWith(tag + "/", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Все теги хранилища и число заметок с каждым: сначала частые.</summary>
    public static List<(string Tag, int Notes)> TagCounts(Note[] notes)
    {
        var counts = new Dictionary<string, (string Name, int N)>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in notes)
            foreach (var t in n.Tags)
                counts[t] = counts.TryGetValue(t, out var c) ? (c.Name, c.N + 1) : (t, 1);
        return counts.Values.OrderByDescending(c => c.N).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => (c.Name, c.N)).ToList();
    }

    /// <summary>По одному месту на заметку с этим тегом (первое вхождение), по названию заметки.</summary>
    public static List<SearchHit> TagHits(Note[] notes, string tag, CancellationToken ct)
    {
        var hits = new List<SearchHit>();
        foreach (var n in notes.Where(n => HasTag(n, tag)).OrderBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            if (ct.IsCancellationRequested) break;
            foreach (var m in Markdown.TagMatches(n.Text))
            {
                string t = m.Value[1..];
                if (!t.Equals(tag, StringComparison.OrdinalIgnoreCase) && !t.StartsWith(tag + "/", StringComparison.OrdinalIgnoreCase)) continue;
                int line = 1;
                for (int i = n.Text.IndexOf('\n'); i >= 0 && i < m.Index; i = n.Text.IndexOf('\n', i + 1)) line++;
                var (snippet, start) = Markdown.LineAround(n.Text, m.Index, m.Length);
                hits.Add(new SearchHit(n, m.Index, line, snippet, start));
                break;
            }
        }
        return hits;
    }

    // ---- замена по всему хранилищу ----

    /// <summary>Заметки, где есть совпадения, и число совпадений в каждой (по сохранённому тексту).</summary>
    public List<(Note Note, int Count)> ReplacePlan(string find, bool matchCase)
    {
        var plan = new List<(Note, int)>();
        foreach (var n in _byPath.Values)
            if (TextFind.Count(n.Text, find, matchCase) is var c and > 0) plan.Add((n, c));
        plan.Sort((a, b) => string.Compare(a.Item1.Rel, b.Item1.Rel, StringComparison.CurrentCultureIgnoreCase));
        return plan;
    }

    /// <summary>Заменяет во всех заметках и сохраняет их. Возвращает изменённые заметки с числом замен и пути, которые записать не удалось.</summary>
    public (List<(Note Note, int Count)> Changed, List<string> Failed) ReplaceAll(string find, string replacement, bool matchCase)
    {
        var changed = new List<(Note, int)>();
        var failed = new List<string>();
        foreach (var (note, _) in ReplacePlan(find, matchCase))
        {
            var (text, count) = TextFind.Replace(note.Text, find, replacement, matchCase);
            if (count == 0) continue;
            try { Save(note, text); changed.Add((note, count)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add(note.Rel); }
        }
        return (changed, failed);
    }

    // ---- запись ----

    public void Save(Note note, string text)
    {
        note.Text = text;
        note.Links = Markdown.LinkKeys(text);
        note.Tags = Markdown.TagKeys(text);
        if (ReadOnly) return;
        string tmp = note.Path + ".leaflet-tmp";
        File.WriteAllText(tmp, text, Utf8);
        File.Move(tmp, note.Path, overwrite: true);
        note.Modified = File.GetLastWriteTime(note.Path);
    }

    /// <summary>Создаёт заметку по имени из ссылки («Имя» или «Папка/Имя»). Повтор имени получает номер.</summary>
    public Note Create(string name, string? folder = null)
    {
        name = name.Replace('\\', '/').Trim().Trim('/');
        int hash = name.IndexOf('#');
        if (hash >= 0) name = name[..hash].Trim();
        if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) name = name[..^3];
        var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Clean).Where(p => p.Length > 0).ToList();
        if (parts.Count == 0) parts.Add(Loc.T("note.untitled"));
        string title = parts[^1];
        string dir = System.IO.Path.Combine([Root, .. (folder ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries), .. parts[..^1]]);
        string path = System.IO.Path.Combine(dir, title + ".md");
        for (int i = 2; File.Exists(path) || _byPath.ContainsKey(path); i++)
            path = System.IO.Path.Combine(dir, $"{title} {i}.md");

        var note = Describe(path);
        note.Text = "";
        if (!ReadOnly)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, "", Utf8);
            note.Modified = File.GetLastWriteTime(path);
        }
        _byPath[note.Path] = note;
        _byTitle = null;
        return note;
    }

    /// <summary>Все папки с заметками («» — корень), для выбора при переносе.</summary>
    public List<string> Folders()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
        foreach (var n in _byPath.Values)
            for (int i = n.Folder.IndexOf('/'); ; i = n.Folder.IndexOf('/', i + 1))
            {
                set.Add(i < 0 ? n.Folder : n.Folder[..i]);
                if (i < 0) break;
            }
        return [.. set];
    }

    /// <summary>Путь будущего файла: папка от корня и имя без «/». Имя после чистки может стать пустым.</summary>
    public (string Path, string Title) Target(string folder, string title)
    {
        title = title.Trim();
        if (title.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) title = title[..^3];
        title = Clean(title.Replace('/', '-').Replace('\\', '-'));
        var parts = folder.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Clean).Where(p => p.Length > 0);
        return (System.IO.Path.Combine([Root, .. parts, title + ".md"]), title);
    }

    /// <summary>
    /// Переименовывает или переносит заметку и правит [[ссылки]] на неё во всех заметках (раздел и подпись сохраняются).
    /// Ссылка с путём остаётся с путём, по имени — по имени, если имя однозначно. Возвращает заметки с изменённым текстом.
    /// </summary>
    public List<Note> Move(Note note, string folder, string title)
    {
        var (path, clean) = Target(folder, title);
        if (clean.Length == 0) throw new IOException(Loc.T("name.empty"));
        if (path == note.Path) return [];
        bool caseOnly = path.Equals(note.Path, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(path) || _byPath.ContainsKey(path)))
            throw new IOException(Loc.T("note.exists", System.IO.Path.GetRelativePath(Root, path).Replace('\\', '/')));

        var refs = new List<(Note From, List<Match> Links)>();
        foreach (var n in _byPath.Values)
        {
            if (!n.Links.Any(k => ResolveKey(k, n) == note)) continue;
            var links = Markdown.WikiLink.Matches(n.Text).Where(m =>
            {
                string key = Markdown.LinkKey(m.Groups[1].Value);
                return key.Length > 0 && ResolveKey(key, n) == note;
            }).ToList();
            refs.Add((n, links));
        }

        string oldPath = note.Path, oldDir = System.IO.Path.GetDirectoryName(oldPath)!;
        if (!ReadOnly)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.Move(note.Path, path);
        }
        _byPath.Remove(note.Path);
        var moved = Describe(path);
        note.Path = moved.Path; note.Rel = moved.Rel; note.Title = moved.Title; note.Folder = moved.Folder;
        _byPath[note.Path] = note;
        _byTitle = null;

        var changed = new List<Note>();
        foreach (var (n, links) in refs)
        {
            string byName = ResolveKey(note.TitleKey, n) == note ? note.Title : note.Rel[..^3];
            var sb = new StringBuilder(n.Text.Length + 64);
            int pos = 0;
            foreach (var m in links)
            {
                var g = m.Groups[1];
                int hash = g.Value.IndexOf('#');
                string target = hash >= 0 ? g.Value[..hash] : g.Value;
                sb.Append(n.Text, pos, g.Index - pos)
                  .Append(target.Contains('/') || target.Contains('\\') ? note.Rel[..^3] : byName)
                  .Append(hash >= 0 ? g.Value[hash..] : "");
                pos = g.Index + g.Length;
            }
            sb.Append(n.Text, pos, n.Text.Length - pos);
            string text = sb.ToString();
            if (text == n.Text) continue;
            Save(n, text);
            changed.Add(n);
        }

        // Пути ![](…) и [](…): у самой заметки — от новой папки, у других — ссылки на её .md.
        string newDir = System.IO.Path.GetDirectoryName(note.Path)!;
        foreach (var n in _byPath.Values.ToList())
        {
            string dir = System.IO.Path.GetDirectoryName(n.Path)!;
            string? Map(string full) => full.Equals(oldPath, StringComparison.OrdinalIgnoreCase) ? note.Path
                : n == note && (File.Exists(full) || Directory.Exists(full)) ? full : null;
            string text = n == note ? Rebase(n.Text, oldDir, newDir, Map) : Rebase(n.Text, dir, dir, Map);
            if (text == n.Text) continue;
            Save(n, text);
            if (!changed.Contains(n)) changed.Add(n);
        }
        return changed;
    }

    /// <summary>Удаляет заметку в корзину Windows. Ссылки на неё остаются (станут ссылками на несуществующую).</summary>
    public void Trash(Note note)
    {
        if (!ReadOnly && File.Exists(note.Path))
        {
            var op = new ShFileOp { Func = 3, From = note.Path + "\0\0", Flags = 0x4 | 0x10 | 0x40 | 0x400 }; // FO_DELETE; тихо, без вопросов, в корзину
            int code = SHFileOperation(ref op);
            if (code != 0 || op.Aborted != 0 || File.Exists(note.Path))
                throw new IOException(Loc.T("trash.fail", code));
        }
        _byPath.Remove(note.Path);
        _byTitle = null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShFileOp
    {
        public nint Hwnd;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        public int Aborted;
        public nint NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    static extern int SHFileOperation(ref ShFileOp op);

    static string Clean(string part)
    {
        var sb = new StringBuilder(part.Length);
        foreach (char c in part.Trim())
            sb.Append(c is '<' or '>' or ':' or '"' or '|' or '?' or '*' || c < ' ' ? '-' : c);
        return sb.ToString().Trim('.', ' ');
    }

    // ---- правки снаружи ----

    /// <summary>Следит за папкой; пачки изменённых путей приходят в <paramref name="onBatch"/> из фонового потока.</summary>
    public void Watch(Action<string[]> onBatch)
    {
        if (ReadOnly) return;
        _watcher = new FileSystemWatcher(Root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
        };
        _watchTimer = new Timer(_ =>
        {
            string[] batch;
            lock (_pending) { batch = [.. _pending]; _pending.Clear(); }
            if (batch.Length > 0) onBatch(batch);
        });
        void Queue(string path)
        {
            if (path.EndsWith(".leaflet-tmp", StringComparison.OrdinalIgnoreCase)) return;
            lock (_pending) _pending.Add(path);
            _watchTimer.Change(300, Timeout.Infinite);
        }
        _watcher.Created += (_, e) => Queue(e.FullPath);
        _watcher.Changed += (_, e) => Queue(e.FullPath);
        _watcher.Deleted += (_, e) => Queue(e.FullPath);
        _watcher.Renamed += (_, e) => { Queue(e.OldFullPath); Queue(e.FullPath); };
        _watcher.EnableRaisingEvents = true;
    }

    public void StopWatching()
    {
        _watcher?.Dispose();
        _watchTimer?.Dispose();
    }

    /// <summary>
    /// Перечитывает пути из <see cref="Watch"/>: заметки с новым текстом, признак изменения списка, пропавшие заметки и переименованные снаружи
    /// (пропала одна, появилась другая с тем же текстом — объект заметки остаётся прежним, меняется путь).
    /// </summary>
    public ExternalChanges Refresh(string[] paths)
    {
        var changed = new List<Note>();
        var removed = new List<Note>();
        var added = new List<Note>();
        bool listChanged = false;
        foreach (var raw in paths)
        {
            string path = System.IO.Path.GetFullPath(raw);
            if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) _files = null; // вложение появилось, пропало или переименовано
            if (File.Exists(path))
            {
                if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || IsHidden(path)) continue;
                var fresh = Read(path);
                if (fresh == null) continue;
                if (_byPath.TryGetValue(path, out var note))
                {
                    if (note.Text == fresh.Text) continue; // своя запись или без изменений
                    note.Text = fresh.Text; note.Links = fresh.Links; note.Tags = fresh.Tags; note.Modified = fresh.Modified;
                    changed.Add(note);
                }
                else { _byPath[path] = fresh; added.Add(fresh); listChanged = true; }
            }
            else if (Directory.Exists(path))
            {
                if (IsHidden(path + "\\x")) continue;
                var files = new List<string>();
                Collect(path, files);
                foreach (var f in files)
                    if (!_byPath.ContainsKey(f) && Read(f) is { } n) { _byPath[n.Path] = n; added.Add(n); listChanged = true; }
            }
            else
            {
                string prefix = path + "\\";
                foreach (var key in _byPath.Keys.Where(k => k.Equals(path, StringComparison.OrdinalIgnoreCase)
                             || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    removed.Add(_byPath[key]);
                    _byPath.Remove(key);
                    listChanged = true;
                }
            }
        }

        var renamed = new List<(Note, string)>();
        foreach (var gone in removed.ToList())
        {
            var same = added.Where(n => n.Text == gone.Text).Take(2).ToList();
            if (same.Count != 1 || removed.Count(r => r.Text == gone.Text) != 1) continue;
            var fresh = same[0];
            string oldRel = gone.Rel;
            _byPath.Remove(fresh.Path);
            gone.Path = fresh.Path; gone.Rel = fresh.Rel; gone.Title = fresh.Title; gone.Folder = fresh.Folder; gone.Modified = fresh.Modified;
            _byPath[gone.Path] = gone;
            added.Remove(fresh);
            removed.Remove(gone);
            renamed.Add((gone, oldRel));
        }
        if (listChanged) _byTitle = null;
        return new ExternalChanges(changed, listChanged, removed, renamed);
    }

    /// <summary>Файл заметки изменён или удалён не нами после последнего чтения или записи (дата и текст отличаются).</summary>
    public bool ChangedOnDisk(Note note)
    {
        if (ReadOnly) return false;
        try
        {
            var info = new FileInfo(note.Path);
            if (!info.Exists) return true;
            if (info.LastWriteTime == note.Modified) return false;
            if (File.ReadAllText(note.Path) != note.Text) return true;
            note.Modified = info.LastWriteTime; // дата новая, текст тот же
            return false;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Записывает пропавшую заметку заново по её пути и возвращает в индекс.</summary>
    public void Readd(Note note, string text)
    {
        if (!ReadOnly) Directory.CreateDirectory(System.IO.Path.GetDirectoryName(note.Path)!);
        Save(note, text);
        _byPath[note.Path] = note;
        _byTitle = null;
    }

    /// <summary>Пустая заметка по пути, которого нет в индексе (восстановление черновика пропавшего файла).</summary>
    public Note Recreate(string path)
    {
        var note = Describe(path);
        Readd(note, "");
        return note;
    }

    /// <summary>Файл пропавшей заметки появился снова: индекс снова указывает на прежний объект (вкладки, история переходов), текст — с диска.</summary>
    public bool Adopt(Note note)
    {
        if (!_byPath.TryGetValue(note.Path, out var fresh)) return false;
        if (fresh != note)
        {
            note.Text = fresh.Text; note.Links = fresh.Links; note.Tags = fresh.Tags; note.Modified = fresh.Modified;
            _byPath[note.Path] = note;
            _byTitle = null;
        }
        return true;
    }

    bool IsHidden(string path) =>
        System.IO.Path.GetRelativePath(Root, path).Split('\\', '/').SkipLast(1).Any(p => p.StartsWith('.'));
}
