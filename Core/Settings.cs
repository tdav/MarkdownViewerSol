namespace Leaflet.Core;

/// <summary>Настройки в %APPDATA%\Leaflet\settings.ini: строки «ключ=значение».</summary>
public sealed class Settings
{
    public const int DefaultAutosaveMs = 800, DefaultHistoryMinutes = 10;
    /// <summary>Папка данных: настройки, черновики (drafts), снимки версий (history). По умолчанию %APPDATA%\Leaflet,
    /// рядом с exe лежит <c>portable.txt</c> — папка <c>data</c> рядом с exe (если в неё можно писать), <c>--data папка</c> перекрывает всё.</summary>
    public static string DefaultDir { get; private set; } = Resolve(null);
    static string DefaultPath = Path.Combine(DefaultDir, "settings.ini");

    public static void UseDataDir(string? dataArg)
    {
        DefaultDir = Resolve(dataArg);
        DefaultPath = Path.Combine(DefaultDir, "settings.ini");
    }

    static string Resolve(string? dataArg)
    {
        if (dataArg != null) return Path.GetFullPath(dataArg);
        try
        {
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt")))
            {
                string dir = Path.Combine(AppContext.BaseDirectory, "data");
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return dir;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Leaflet");
    }
    /// <summary>Настройки окна «Настройки»; в режиме только чтения (замеры, самопроверка) из файла не берутся.</summary>
    static readonly string[] PrefKeys = ["font", "size", "width", "autosave", "history"];
    /// <summary>Тоже не берутся в режиме только чтения, но «Сбросить» их не трогает.</summary>
    static readonly string[] OtherKeys = ["attach", "lang"];
    readonly Dictionary<string, string> _values = new();
    string _path = DefaultPath;

    public bool ReadOnly { get; init; }

    public static Settings Load(bool readOnly, string? path = null)
    {
        var s = new Settings { ReadOnly = readOnly, _path = path ?? DefaultPath };
        try
        {
            if (File.Exists(s._path))
                foreach (var line in File.ReadAllLines(s._path))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0 && !(readOnly && (PrefKeys.Contains(line[..eq]) || OtherKeys.Contains(line[..eq]) || line.StartsWith("key.", StringComparison.Ordinal) || line.StartsWith("find.", StringComparison.Ordinal))))s._values[line[..eq]] = line[(eq + 1)..];
                }
        }
        catch (IOException) { }
        return s;
    }

    public string? this[string key]
    {
        get => _values.GetValueOrDefault(key);
        set { if (value == null) _values.Remove(key); else _values[key] = value; }
    }

    public bool Flag(string key, bool fallback) => this[key] is { } v ? v == "1" : fallback;
    public void SetFlag(string key, bool value) => this[key] = value ? "1" : "0";

    public double Number(string key, double fallback) =>
        double.TryParse(this[key], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    void SetNumber(string key, double value) => this[key] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Шрифт текста заметок; null — шрифт темы.</summary>
    public string? Font { get => string.IsNullOrWhiteSpace(this["font"]) ? null : this["font"]; set => this["font"] = value; }
    /// <summary>Размер текста заметок; 0 — размер темы.</summary>
    public double FontSize { get { double v = Number("size", 0); return v is >= 8 and <= 48 ? v : 0; } set => SetNumber("size", value); }
    /// <summary>Ширина колонки текста в пикселях; 0 — во всю ширину редактора.</summary>
    public int TextWidth { get { double v = Number("width", 0); return v >= 320 ? (int)v : 0; } set => SetNumber("width", value); }
    /// <summary>Пауза после правки до автосохранения, мс; 0 — не сохранять само.</summary>
    public int AutosaveMs { get { double v = Number("autosave", DefaultAutosaveMs); return v <= 0 ? 0 : (int)Math.Clamp(v, 100, 60000); } set => SetNumber("autosave", value); }
    /// <summary>Снимок версии при сохранении не чаще раза в столько минут; 0 — только при закрытии.</summary>
    public int HistoryMinutes { get { double v = Number("history", DefaultHistoryMinutes); return v <= 0 ? 0 : (int)Math.Clamp(v, 1, 1440); } set => SetNumber("history", value); }

    /// <summary>Папка вложений: имя — рядом с заметкой, «/имя» — от корня хранилища (см. <see cref="Vault.AttachFolder"/>).</summary>
    public string AttachFolder
    {
        get => string.IsNullOrWhiteSpace(this["attach"]) ? DefaultAttach : this["attach"]!.Trim();
        set => this["attach"] = string.IsNullOrWhiteSpace(value) || value.Trim() == DefaultAttach ? null : value.Trim();
    }
    public const string DefaultAttach = "assets";

    /// <summary>Язык интерфейса: «ru» или «en»; null — не выбран (язык системы, иначе английский; см. <see cref="Loc.Detect"/>).</summary>
    public string? Lang { get => this["lang"] is Loc.Ru or Loc.En ? this["lang"] : null; set => this["lang"] = value; }

    public void ResetPrefs() { foreach (var k in PrefKeys) this[k] = null; }

    public void ClearPrefix(string prefix)
    {
        foreach (var k in _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) _values.Remove(k);
    }

    public void Save()
    {
        if (ReadOnly) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllLines(_path, _values.Select(kv => $"{kv.Key}={kv.Value}"));
        }
        catch (IOException) { }
    }
}
