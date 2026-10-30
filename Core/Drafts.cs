using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Leaflet.Core;

/// <summary>Черновик несохранённой правки. <see cref="BaseHash"/> — от текста файла, на котором он основан.</summary>
public sealed record Draft(string File, string Path, string Vault, DateTime Time, string Text, string BaseHash, int Pid, long Started);

/// <summary>
/// Черновики несохранённых правок в <c>%APPDATA%\Leaflet\drafts</c> (файл на заметку). Пишутся в фоне по очереди;
/// сохранение заметки удаляет черновик. Черновики живого процесса (этого или другого окна) восстановить не предлагаются.
/// </summary>
public sealed class Drafts(string dir)
{
    static readonly UTF8Encoding Utf8 = new(false);
    // Кириллица как есть: черновик можно прочитать и в блокноте.
    static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly int Pid = Environment.ProcessId;
    static readonly long Started = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    public string Dir { get; } = dir;

    readonly object _lock = new();
    Task _tail = Task.CompletedTask;

    public string FileFor(string path) =>
        System.IO.Path.Combine(Dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..20].ToLowerInvariant() + ".json");

    void Enqueue(Action work)
    {
        lock (_lock) _tail = _tail.ContinueWith(_ =>
        {
            try { work(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }, TaskScheduler.Default);
    }

    public void Flush()
    {
        Task t;
        lock (_lock) t = _tail;
        t.Wait();
    }

    public void Write(string vault, string path, string text, string baseText)
    {
        var time = DateTime.Now;
        Enqueue(() =>
        {
            Directory.CreateDirectory(Dir);
            string file = FileFor(path), tmp = file + ".tmp";
            var draft = new Draft(file, path, vault, time, text, Hash(baseText), Pid, Started);
            System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(draft, Json), Utf8);
            System.IO.File.Move(tmp, file, overwrite: true);
        });
    }

    public void Delete(string path)
    {
        string file = FileFor(path);
        Enqueue(() => System.IO.File.Delete(file));
    }

    public static void Discard(Draft d)
    {
        try { System.IO.File.Delete(d.File); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Черновики заметок этого хранилища, оставшиеся от закрытых или упавших окон; новые первыми.</summary>
    public List<Draft> Pending(string vault)
    {
        Flush();
        var list = new List<Draft>();
        if (!Directory.Exists(Dir)) return list;
        foreach (var f in Directory.EnumerateFiles(Dir, "*.json"))
        {
            Draft? d;
            try { d = JsonSerializer.Deserialize<Draft>(System.IO.File.ReadAllText(f)); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { continue; }
            if (d == null || !d.Vault.Equals(vault, StringComparison.OrdinalIgnoreCase) || Alive(d)) continue;
            list.Add(d with { File = f });
        }
        list.Sort((a, b) => b.Time.CompareTo(a.Time));
        return list;
    }

    static bool Alive(Draft d)
    {
        try
        {
            using var p = Process.GetProcessById(d.Pid);
            return p.StartTime.ToUniversalTime().Ticks == d.Started;
        }
        catch (Exception) { return false; } // нет процесса или нет доступа к нему
    }

    public static string Hash(string text) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)));
}
