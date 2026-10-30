using System.Globalization;

namespace Leaflet.Core;

/// <summary>
/// Язык интерфейса: строки лежат в <see cref="Strings"/> (таблица по языкам, ключ — id), <see cref="T(string)"/> берёт строку
/// текущего языка. Смена языка (<see cref="Set"/>) сразу зовёт <see cref="Changed"/> — окно перерисовывает подписи без перезапуска.
/// </summary>
public static class Loc
{
    public const string Ru = "ru", En = "en";
    public static readonly string[] Languages = [Ru, En];

    static Dictionary<string, string> _table = Strings.En;

    public static string Lang { get; private set; } = En;
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");
    public static event Action? Changed;

    /// <summary>Язык из настроек; нет (или неизвестный) — язык системы, если он поддерживается, иначе английский.</summary>
    public static string Detect(string? saved, CultureInfo? system = null)
    {
        if (saved is Ru or En) return saved;
        return (system ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName == Ru ? Ru : En;
    }

    /// <summary>Ставит язык без события (запуск); неизвестное значение — английский.</summary>
    public static void Init(string lang)
    {
        Lang = lang == Ru ? Ru : En;
        _table = Lang == Ru ? Strings.Ru : Strings.En;
        Culture = CultureInfo.GetCultureInfo(Lang == Ru ? "ru-RU" : "en-US");
    }

    /// <summary>Меняет язык на лету; false — язык тот же.</summary>
    public static bool Set(string lang)
    {
        if (lang != Ru && lang != En || lang == Lang) return false;
        Init(lang);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Другой язык из <see cref="Languages"/> (для переключателя из двух).</summary>
    public static string Other => Lang == Ru ? En : Ru;

    public static string T(string id) =>
        _table.TryGetValue(id, out var s) ? s : Strings.En.TryGetValue(id, out var e) ? e : id;

    public static string T(string id, params object?[] args) => string.Format(Culture, T(id), args);

    /// <summary>«5 заметок» / «5 notes»: id — запись с формами через «|» (русский — три формы, английский — две).</summary>
    public static string Plural(long n, string id)
    {
        var forms = T(id).Split('|');
        string word;
        if (Lang == Ru && forms.Length >= 3)
        {
            long d = n % 100;
            word = d is >= 11 and <= 14 ? forms[2] : (n % 10) switch { 1 => forms[0], 2 or 3 or 4 => forms[1], _ => forms[2] };
        }
        else word = n == 1 ? forms[0] : forms[^1];
        return string.Format(Culture, "{0:N0} {1}", n, word).Replace(' ', ' ');
    }

    /// <summary>Десятичный разделитель языка: «14,5» по-русски, «14.5» по-английски.</summary>
    public static string Dec(string invariantNumber) => Lang == Ru ? invariantNumber.Replace('.', ',') : invariantNumber;
}
