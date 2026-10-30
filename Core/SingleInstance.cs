using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Leaflet.Core;

/// <summary>
/// Один экземпляр на папку данных: первый процесс держит мьютекс и слушает именованный канал, повторный запуск
/// передаёт ему пути из командной строки и выходит. Папка данных входит в имя: портативная копия и обычная
/// установка (и тесты с <c>--data</c>) друг друга не трогают.
/// </summary>
static class SingleInstance
{
    const string Magic = "LEAF1";
    const char End = '\u0004';
    static Mutex? _mutex;
    static string _pipe = "";

    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

    /// <summary>Режимы замеров и самопроверки живут сами по себе: с другим экземпляром их не смешивать.</summary>
    public static bool Applies(string[] args) =>
        !args.Any(a => a is "--bench" or "--selftest" or "--fontcheck" or "--crashtest" or "--gen" or "--new" or "--dpicheck");

    /// <summary>Пути из командной строки (файлы и папки), относительные — от текущей папки.</summary>
    public static List<string> Paths(string[] args)
    {
        var list = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--data" or "--lang" or "--bench" or "--fontcheck" or "--selftest" or "--only" or "--dpicheck") { i++; continue; }
            if (args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            try { list.Add(Path.GetFullPath(args[i])); } catch (ArgumentException) { }
        }
        return list;
    }

    /// <summary>true — путь передан уже запущенному окну, этому процессу работать не нужно.</summary>
    public static bool TryForward(string[] args)
    {
        string name = Name();
        _pipe = "Leaflet.Pipe." + name;
        _mutex = new Mutex(true, @"Local\Leaflet.Mutex." + name, out bool first);
        if (first) return false;

        var msg = Magic + "\n" + string.Join("\n", Paths(args)) + "\n" + End; // канал без полузакрытия: конец сообщения — знак EOT
        var deadline = DateTime.UtcNow.AddSeconds(6); // первый процесс мог только запуститься и ещё не открыл канал
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var c = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
                c.Connect(500);
                AllowSetForegroundWindow(-1); // нас только что запустил пользователь — разрешаем окну выйти вперёд
                var bytes = new UTF8Encoding(false).GetBytes(msg);
                c.Write(bytes, 0, bytes.Length);
                c.Flush();
                c.WaitForPipeDrain();
                var ack = new byte[2];
                if (c.Read(ack, 0, 2) == 2) return true;
            }
            catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException) { }
            Thread.Sleep(100);
        }
        _mutex.Dispose();
        _mutex = null; // первый экземпляр завис — открываемся сами, без слушателя
        return false;
    }

    /// <summary>Слушатель первого экземпляра: <paramref name="onPaths"/> вызывается в чужом потоке для каждого повторного запуска.</summary>
    public static void Listen(Action<List<string>> onPaths)
    {
        if (_mutex == null) return;
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var s = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    s.WaitForConnection();
                    using var r = new StreamReader(s, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                    var sb = new StringBuilder();
                    for (int ch; (ch = r.Read()) >= 0 && ch != End;) sb.Append((char)ch);
                    var text = sb.ToString();
                    var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 0 && lines[0] == Magic) onPaths([.. lines.Skip(1)]);
                    s.Write([(byte)'o', (byte)'k'], 0, 2);
                    s.Flush();
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { return; }
            }
        })
        { IsBackground = true, Name = "Leaflet.Pipe" };
        t.Start();
    }

    static string Name()
    {
        var hash = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(Settings.DefaultDir.TrimEnd('\\').ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 8);
    }
}
