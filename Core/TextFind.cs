using System.Text;

namespace Leaflet.Core;

/// <summary>Поиск и замена подстроки: общая основа для поиска в заметке и замены по всему хранилищу.</summary>
public static class TextFind
{
    public const int MaxMatches = 100_000;

    static StringComparison Mode(bool matchCase) => matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Начала всех непересекающихся совпадений (длина у всех равна длине запроса).</summary>
    public static List<int> Matches(string text, string query, bool matchCase, int max = MaxMatches)
    {
        var list = new List<int>();
        if (query.Length == 0) return list;
        var mode = Mode(matchCase);
        for (int i = text.IndexOf(query, mode); i >= 0 && list.Count < max; i = text.IndexOf(query, i + query.Length, mode))
            list.Add(i);
        return list;
    }

    public static int Count(string text, string query, bool matchCase)
    {
        if (query.Length == 0) return 0;
        var mode = Mode(matchCase);
        int n = 0;
        for (int i = text.IndexOf(query, mode); i >= 0; i = text.IndexOf(query, i + query.Length, mode)) n++;
        return n;
    }

    /// <summary>Текст с заменёнными совпадениями и число замен.</summary>
    public static (string Text, int Count) Replace(string text, string query, string replacement, bool matchCase)
    {
        if (query.Length == 0) return (text, 0);
        var mode = Mode(matchCase);
        int first = text.IndexOf(query, mode);
        if (first < 0) return (text, 0);
        var sb = new StringBuilder(text.Length + 64);
        int pos = 0, count = 0;
        for (int i = first; i >= 0; i = text.IndexOf(query, pos, mode))
        {
            sb.Append(text, pos, i - pos).Append(replacement);
            pos = i + query.Length;
            count++;
        }
        sb.Append(text, pos, text.Length - pos);
        return (sb.ToString(), count);
    }
}
