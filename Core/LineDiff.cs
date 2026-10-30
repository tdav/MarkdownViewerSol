namespace Leaflet.Core;

public enum DiffKind { Same, Del, Add, Gap }

public readonly record struct DiffLine(DiffKind Kind, string Text);

/// <summary>Построчное сравнение (Майерс): общие начало и конец отрезаются, середина — кратчайший путь правок.</summary>
public static class LineDiff
{
    /// <summary>Больше правок в середине — она показывается целиком как «удалено, потом добавлено» (память трассы ~ D²).</summary>
    const int MaxEdits = 3000;

    public static List<DiffLine> Compare(string before, string after)
    {
        var a = Split(before);
        var b = Split(after);
        int pre = 0;
        while (pre < a.Length && pre < b.Length && a[pre] == b[pre]) pre++;
        int suf = 0;
        while (suf < a.Length - pre && suf < b.Length - pre && a[^(suf + 1)] == b[^(suf + 1)]) suf++;
        var result = new List<DiffLine>(Math.Max(a.Length, b.Length) + 8);
        for (int i = 0; i < pre; i++) result.Add(new(DiffKind.Same, a[i]));
        Middle(a[pre..^suf], b[pre..^suf], result);
        for (int i = a.Length - suf; i < a.Length; i++) result.Add(new(DiffKind.Same, a[i]));
        return result;
    }

    static string[] Split(string text) => text.Length == 0 ? [] : text.ReplaceLineEndings("\n").Split('\n');

    static void Middle(string[] a, string[] b, List<DiffLine> result)
    {
        int n = a.Length, m = b.Length, max = n + m;
        if (n == 0 || m == 0 || max == 0)
        {
            foreach (var s in a) result.Add(new(DiffKind.Del, s));
            foreach (var s in b) result.Add(new(DiffKind.Add, s));
            return;
        }
        int off = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();
        int found = -1;
        for (int d = 0; d <= max && d <= MaxEdits && found < 0; d++)
        {
            for (int k = -d; k <= d; k += 2)
            {
                bool down = k == -d || (k != d && v[off + k - 1] < v[off + k + 1]);
                int x = down ? v[off + k + 1] : v[off + k - 1] + 1, y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                v[off + k] = x;
                if (x >= n && y >= m) { found = d; break; }
            }
            trace.Add(v[(off - d)..(off + d + 1)]);
        }
        if (found < 0)
        {
            foreach (var s in a) result.Add(new(DiffKind.Del, s));
            foreach (var s in b) result.Add(new(DiffKind.Add, s));
            return;
        }

        var back = new List<DiffLine>(n + m);
        int cx = n, cy = m;
        for (int d = found; d > 0; d--)
        {
            var prev = trace[d - 1]; // значения шага d-1 для k ∈ [-(d-1), d-1], индекс k + d - 1
            int k = cx - cy;
            bool down = k == -d || (k != d && prev[k - 1 + d - 1] < prev[k + 1 + d - 1]);
            int pk = down ? k + 1 : k - 1;
            int px = prev[pk + d - 1], py = px - pk;
            int sx = down ? px : px + 1, sy = sx - k; // точка после шага правки
            while (cx > sx && cy > sy) { back.Add(new(DiffKind.Same, a[--cx])); cy--; }
            if (down) back.Add(new(DiffKind.Add, b[--cy]));
            else back.Add(new(DiffKind.Del, a[--cx]));
        }
        while (cx > 0 && cy > 0) { back.Add(new(DiffKind.Same, a[--cx])); cy--; }
        back.Reverse();
        result.AddRange(back);
    }

    /// <summary>Только изменённые места с <paramref name="context"/> строками вокруг; пропуски — строка <see cref="DiffKind.Gap"/>.</summary>
    public static List<DiffLine> Hunks(List<DiffLine> lines, int context = 3)
    {
        var keep = new bool[lines.Count];
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Kind != DiffKind.Same)
                for (int j = Math.Max(0, i - context); j <= Math.Min(lines.Count - 1, i + context); j++) keep[j] = true;
        var result = new List<DiffLine>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (keep[i]) { result.Add(lines[i]); continue; }
            int start = i;
            while (i + 1 < lines.Count && !keep[i + 1]) i++;
            if (start > 0 || i < lines.Count - 1) result.Add(new(DiffKind.Gap, Loc.T("diff.gap", i - start + 1)));
        }
        return result.Count == 1 && result[0].Kind == DiffKind.Gap ? [] : result;
    }

    public static (int Added, int Removed) Count(List<DiffLine> lines) =>
        (lines.Count(l => l.Kind == DiffKind.Add), lines.Count(l => l.Kind == DiffKind.Del));
}
