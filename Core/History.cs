using System.Security.Cryptography;
using System.Text;

namespace Leaflet.Core;

/// <summary>Снимок версии заметки: файл <c>ГГГГММДД-ччммсс-ммм.причина.md</c> в папке заметки.</summary>
public sealed record Snapshot(DateTime Time, string Reason, string File, long Size)
{
    public string ReasonText => Reason switch
    {
        "save" => Loc.T("snap.save"),
        "open" => Loc.T("snap.open"),
        "close" => Loc.T("snap.close"),
        "disk" => Loc.T("snap.disk"),
        "mine" => Loc.T("snap.mine"),
        "restore" => Loc.T("snap.restore"),
        "draft" => Loc.T("snap.draft"),
        _ => Reason,
    };
}

/// <summary>
/// Снимки версий в <c>%APPDATA%\Leaflet\history\&lt;хранилище&gt;\&lt;заметка&gt;</c>: пишутся в фоне по очереди, не чаще интервала
/// (кроме снимков по причине), одинаковый с последним текст не пишется. Предел — <see cref="MaxPerNote"/> на заметку и <see cref="MaxBytes"/> на всё.
/// </summary>
public sealed class History(string dir)
{
    static readonly UTF8Encoding Utf8 = new(false);
    public int MaxPerNote = 50;
    public long MaxBytes = 100L << 20;
    public string Dir { get; } = dir;

    readonly object _lock = new();
    Task _tail = Task.CompletedTask;
    // Только из фоновой очереди:
    readonly Dictionary<string, (DateTime Time, int Length, int Hash)> _last = new(StringComparer.OrdinalIgnoreCase);
    long _bytes = -1;

    public string Folder(string vaultRoot, string rel)
    {
        string name = new string(Path.GetFileName(vaultRoot.TrimEnd('\\', '/')).Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        return Path.Combine(Dir, $"{name}-{Key(vaultRoot)[..8]}", Key(rel));
    }

    static string Key(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s.ToLowerInvariant())))[..16].ToLowerInvariant();

    void Enqueue(Action work)
    {
        lock (_lock) _tail = _tail.ContinueWith(_ =>
        {
            try { work(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }, TaskScheduler.Default);
    }

    /// <summary>Ждёт, пока очередь запишет всё поставленное.</summary>
    public void Flush()
    {
        Task t;
        lock (_lock) t = _tail;
        t.Wait();
    }

    /// <summary>
    /// Ставит снимок в очередь. <paramref name="interval"/> &gt; 0 — пропустить, если прошлый снимок моложе.
    /// <paramref name="before"/> — текст до правок: у заметки без снимков он записывается первым, чтобы можно было вернуться к началу.
    /// </summary>
    public void Add(string vaultRoot, string rel, string text, string reason, TimeSpan interval = default, string? before = null)
    {
        string folder = Folder(vaultRoot, rel);
        var now = DateTime.Now;
        Enqueue(() =>
        {
            if (before is { Length: > 0 } && before != text && Last(folder) == null) Write(folder, rel, before, "open", default, now.AddMilliseconds(-1));
            Write(folder, rel, text, reason, interval, now);
        });
    }

    void Write(string folder, string rel, string text, string reason, TimeSpan interval, DateTime now)
    {
        var last = Last(folder);
        int hash = text.GetHashCode();
        if (last is { } l && l.Length == text.Length && l.Hash == hash) return; // такой же, как последний
        if (interval > TimeSpan.Zero && last is { } p && now - p.Time < interval) return;
        Directory.CreateDirectory(folder);
        string nameFile = Path.Combine(folder, "name.txt");
        if (!File.Exists(nameFile) || File.ReadAllText(nameFile) != rel) File.WriteAllText(nameFile, rel, Utf8);
        now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond); // имя по миллисекундам: снимки подряд не затирают друг друга
        if (last is { } t && now <= t.Time) now = t.Time.AddMilliseconds(1);
        string file = Path.Combine(folder, $"{now:yyyyMMdd-HHmmss-fff}.{reason}.md");
        File.WriteAllText(file, text, Utf8);
        _last[folder] = (now, text.Length, hash);
        long size = new FileInfo(file).Length;

        var all = Files(folder);
        for (int i = 0; i < all.Count - MaxPerNote; i++)
        {
            size -= all[i].Size;
            File.Delete(all[i].File);
        }
        if (_bytes < 0) _bytes = Total();
        else _bytes += size;
        if (_bytes > MaxBytes) Trim();
    }

    (DateTime Time, int Length, int Hash)? Last(string folder)
    {
        if (_last.TryGetValue(folder, out var cached)) return cached;
        var files = Files(folder);
        if (files.Count == 0) return null;
        string text = File.ReadAllText(files[^1].File);
        return _last[folder] = (files[^1].Time, text.Length, text.GetHashCode());
    }

    /// <summary>Снимки заметки, старые первыми.</summary>
    static List<Snapshot> Files(string folder)
    {
        var list = new List<Snapshot>();
        if (!Directory.Exists(folder)) return list;
        foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*.md"))
        {
            var parts = f.Name.Split('.');
            if (parts.Length != 3 || !DateTime.TryParseExact(parts[0], "yyyyMMdd-HHmmss-fff", null, System.Globalization.DateTimeStyles.None, out var t)) continue;
            list.Add(new Snapshot(t, parts[1], f.FullName, f.Length));
        }
        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        return list;
    }

    long Total() => Directory.Exists(Dir) ? new DirectoryInfo(Dir).EnumerateFiles("*.md", SearchOption.AllDirectories).Sum(f => f.Length) : 0;

    /// <summary>Сверх предела удаляет самые старые снимки по всему хранилищу истории; последний снимок каждой заметки остаётся.</summary>
    void Trim()
    {
        var notes = new DirectoryInfo(Dir).EnumerateDirectories("*", SearchOption.AllDirectories).Select(d => Files(d.FullName)).Where(l => l.Count > 1).ToList();
        var old = notes.SelectMany(l => l.Take(l.Count - 1)).OrderBy(s => s.Time).ToList();
        _bytes = Total();
        foreach (var s in old)
        {
            if (_bytes <= MaxBytes * 9 / 10) break;
            File.Delete(s.File);
            _bytes -= s.Size;
        }
    }

    /// <summary>Снимки заметки, новые первыми (ждёт очередь).</summary>
    public List<Snapshot> List(string vaultRoot, string rel)
    {
        Flush();
        var list = Files(Folder(vaultRoot, rel));
        list.Reverse();
        return list;
    }

    public static string Read(Snapshot s) => File.ReadAllText(s.File);

    /// <summary>Заметка переименована или перенесена: снимки переезжают в папку нового имени.</summary>
    public void Rename(string vaultRoot, string oldRel, string newRel)
    {
        string from = Folder(vaultRoot, oldRel), to = Folder(vaultRoot, newRel);
        if (from.Equals(to, StringComparison.OrdinalIgnoreCase)) return;
        Enqueue(() =>
        {
            if (!Directory.Exists(from)) return;
            Directory.CreateDirectory(to);
            foreach (var f in Directory.EnumerateFiles(from, "*.md")) File.Move(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
            File.WriteAllText(Path.Combine(to, "name.txt"), newRel, Utf8);
            Directory.Delete(from, recursive: true);
            _last.Remove(from);
            _last.Remove(to);
        });
    }
}
