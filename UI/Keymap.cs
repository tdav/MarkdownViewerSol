using Avalonia.Input;
using Leaflet.Core;

namespace Leaflet.UI;

/// <summary>Сочетание клавиш: клавиша и Ctrl/Shift/Alt. В тексте — «Ctrl+Shift+F», «Alt+←», «Ctrl+,», «F2».</summary>
readonly record struct Gesture(Key Key, KeyModifiers Mods)
{
    static readonly (Key Key, string Name)[] Names =
    [
        (Key.Tab, "Tab"), (Key.Space, "Space"), (Key.Left, "←"), (Key.Right, "→"), (Key.Up, "↑"), (Key.Down, "↓"),
        (Key.Home, "Home"), (Key.End, "End"), (Key.PageUp, "PageUp"), (Key.PageDown, "PageDown"), (Key.Insert, "Insert"),
        (Key.OemComma, ","), (Key.OemPeriod, "."), (Key.OemMinus, "-"), (Key.OemPlus, "Plus"),
        (Key.OemOpenBrackets, "["), (Key.OemCloseBrackets, "]"), (Key.OemQuestion, "/"), (Key.OemTilde, "`"),
    ];
    /// <summary>Клавиши, которыми редактор двигает курсор: с Ctrl и Shift они ему нужны.</summary>
    static readonly Key[] Moving = [Key.Left, Key.Right, Key.Up, Key.Down, Key.Home, Key.End, Key.PageUp, Key.PageDown, Key.Space, Key.Insert];
    static readonly Key[] EditorKeys = [Key.A, Key.C, Key.V, Key.X, Key.Z, Key.Y];

    public static string? NameOf(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return key.ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.F1 and <= Key.F12) return key.ToString();
        foreach (var (k, n) in Names) if (k == key) return n;
        return null;
    }

    static Key? KeyOf(string name)
    {
        foreach (var k in Enum.GetValues<Key>()) if (NameOf(k) is { } n && n.Equals(name, StringComparison.OrdinalIgnoreCase)) return k;
        return name.ToLowerInvariant() switch { "left" => Key.Left, "right" => Key.Right, "up" => Key.Up, "down" => Key.Down, _ => null };
    }

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Mods.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (Mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (Mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        parts.Add(NameOf(Key) ?? Key.ToString());
        return string.Join("+", parts);
    }

    public static Gesture? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var mods = KeyModifiers.None;
        foreach (var m in parts[..^1])
        {
            switch (m.ToLowerInvariant())
            {
                case "ctrl": mods |= KeyModifiers.Control; break;
                case "shift": mods |= KeyModifiers.Shift; break;
                case "alt": mods |= KeyModifiers.Alt; break;
                default: return null;
            }
        }
        return KeyOf(parts[^1]) is { } key ? new Gesture(key, mods) : null;
    }

    /// <summary>Нажатие как сочетание; null — это только модификатор, клавиша без названия или нажата Win.</summary>
    public static Gesture? From(KeyEventArgs e) =>
        e.KeyModifiers.HasFlag(KeyModifiers.Meta) || NameOf(e.Key) == null ? null
            : new Gesture(e.Key, e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt));

    /// <summary>Почему сочетание назначить нельзя; null — можно.</summary>
    public string? Problem()
    {
        bool ctrl = Mods.HasFlag(KeyModifiers.Control), alt = Mods.HasFlag(KeyModifiers.Alt);
        if (Key is >= Key.F1 and <= Key.F12) return null;
        if (!ctrl && !alt) return Loc.T("key.p.mod");
        if (ctrl && alt) return Loc.T("key.p.altgr");
        if (Mods == KeyModifiers.Control && Key is >= Key.D1 and <= Key.D9) return Loc.T("key.p.tabs");
        if (ctrl && (Mods == KeyModifiers.Control && EditorKeys.Contains(Key) || Mods == (KeyModifiers.Control | KeyModifiers.Shift) && Key == Key.Z))
            return Loc.T("key.p.editor");
        if (ctrl && Moving.Contains(Key)) return Loc.T("key.p.moving");
        if (alt && Key == Key.Tab) return Loc.T("key.p.alttab");
        return null;
    }
}

/// <summary>Команда, которой можно назначить сочетание.</summary>
sealed record KeyAction(string Id, string? Default)
{
    /// <summary>Название команды на языке интерфейса (строка act.&lt;id&gt; в <see cref="Strings"/>).</summary>
    public string Title => Loc.T("act." + Id);
}

/// <summary>
/// Сочетания клавиш: список команд с сочетаниями по умолчанию и переопределения из settings.ini
/// (строка «key.команда=Ctrl+K», «none» — не назначено; значение по умолчанию не хранится).
/// </summary>
sealed class Keymap
{
    public static readonly KeyAction[] Actions =
    [
        new("palette", "Ctrl+P"),
        new("open", "Ctrl+O"),
        new("new", "Ctrl+N"),
        new("search", "Ctrl+Shift+F"),
        new("find", "Ctrl+F"),
        new("replace", "Ctrl+H"),
        new("findnext", "F3"),
        new("findprev", "Shift+F3"),
        new("replaceall", "Ctrl+Shift+H"),
        new("read", "Ctrl+E"),
        new("folder", "Ctrl+Shift+O"),
        new("rename", "F2"),
        new("export", "Ctrl+Shift+E"),
        new("save", "Ctrl+S"),
        new("close", "Ctrl+W"),
        new("next", "Ctrl+Tab"),
        new("prev", "Ctrl+Shift+Tab"),
        new("back", "Alt+←"),
        new("forward", "Alt+→"),
        new("settings", "Ctrl+,"),
        new("keys", "F1"),
        new("sidebar", null),
        new("right", null),
        new("theme", null),
    ];

    /// <summary>То, что работает всегда и не меняется (для справки).</summary>
    public static (string Title, string Keys)[] Fixed =>
    [
        (Loc.T("fix.tabnum"), "Ctrl+1…9"),
        (Loc.T("fix.undo"), "Ctrl+Z / Ctrl+Y"),
        (Loc.T("fix.delete"), "Delete"),
        (Loc.T("fix.findnav"), "Enter / Shift+Enter"),
        (Loc.T("fix.complete"), "Enter / Tab"),
        (Loc.T("fix.esc"), "Esc"),
        (Loc.T("fix.link"), Loc.T("fix.ctrlclick")),
    ];

    public const string Prefix = "key.";
    readonly Settings _settings;

    /// <summary>Сочетания главного окна: их читают подписи в списках и подсказки.</summary>
    public static Keymap Current { get; set; } = null!;
    public event Action? Changed;

    public Keymap(Settings settings) => _settings = settings;

    public static KeyAction Find(string id) => Actions.First(a => a.Id == id);

    public Gesture? Get(string id)
    {
        string? v = _settings[Prefix + id];
        if (v == null) return Gesture.Parse(Find(id).Default);
        if (v == "none") return null;
        return Gesture.Parse(v) is { } g && g.Problem() == null ? g : Gesture.Parse(Find(id).Default);
    }

    /// <summary>Сочетание текстом; пусто — не назначено.</summary>
    public string Hint(string id) => Get(id)?.ToString() ?? "";

    /// <summary>Подпись для подсказки: «Новая заметка (Ctrl+N)».</summary>
    public string Tip(string title, string id) => Hint(id) is { Length: > 0 } h ? $"{title} ({h})" : title;

    public string? IdOf(Gesture g) => Actions.FirstOrDefault(a => Get(a.Id) == g)?.Id;

    public string? Match(KeyEventArgs e) => Gesture.From(e) is { } g ? IdOf(g) : null;

    /// <summary>Назначает сочетание (null — снять). Возвращает причину отказа или null, если назначено.</summary>
    public string? Set(string id, Gesture? gesture)
    {
        if (gesture is { } g)
        {
            if (g.Problem() is { } problem) return problem;
            if (IdOf(g) is { } other && other != id) return Loc.T("key.taken", Find(other).Title);
        }
        var def = Gesture.Parse(Find(id).Default);
        _settings[Prefix + id] = gesture == def ? null : gesture?.ToString() ?? "none";
        Changed?.Invoke();
        return null;
    }

    public void ResetAll()
    {
        _settings.ClearPrefix(Prefix);
        Changed?.Invoke();
    }

    public bool IsDefault(string id) => _settings[Prefix + id] == null;
}
