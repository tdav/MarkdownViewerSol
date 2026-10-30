using System.Buffers.Binary;

namespace Leaflet.Core;

/// <summary>
/// Урезание шрифта TrueType до нужных знаков. Skia из SkiaSharp собран без урезателя и встраивает в PDF шрифт целиком
/// (Segoe UI — ~950 КБ на начертание), поэтому PDF пишется шрифтом, собранным здесь: номера глифов прежние,
/// у ненужных глифов пустые контуры, таблицы для вёрстки (GSUB, GPOS, kern…) выброшены, cmap — только нужные знаки.
/// </summary>
static class FontSubset
{
    static readonly string[] Copied = ["head", "hhea", "hmtx", "maxp", "name", "OS/2", "cvt ", "fpgm", "prep", "gasp"];

    /// <returns>Новый файл шрифта или null, если шрифт не TrueType (CFF) или разобрать его не удалось.</returns>
    public static byte[]? Make(byte[] font, int ttcIndex, IReadOnlyList<(int Cp, ushort Gid)> map)
    {
        try
        {
            int start = 0;
            if (Tag(font, 0) == "ttcf") start = (int)U32(font, 12 + 4 * ttcIndex);
            if (U32(font, start) is not (0x00010000 or 0x74727565)) return null; // 'OTTO' (CFF) не трогаем
            int count = U16(font, start + 4);
            var tables = new Dictionary<string, (int Off, int Len)>();
            for (int i = 0; i < count; i++)
            {
                int rec = start + 12 + 16 * i;
                tables[Tag(font, rec)] = ((int)U32(font, rec + 8), (int)U32(font, rec + 12));
            }
            if (!new[] { "head", "hhea", "hmtx", "maxp", "loca", "glyf" }.All(tables.ContainsKey)) return null;

            int locFormat = (short)U16(font, tables["head"].Off + 50);
            int glyphs = U16(font, tables["maxp"].Off + 4);
            int loca = tables["loca"].Off, glyf = tables["glyf"].Off;
            int At(int g) => locFormat == 0 ? U16(font, loca + 2 * g) * 2 : (int)U32(font, loca + 4 * g);

            // Нужные глифы и части составных.
            var keep = new HashSet<int>();
            void Visit(int g)
            {
                if (g >= glyphs || !keep.Add(g)) return;
                int s = At(g), e = At(g + 1);
                if (e - s < 10 || (short)U16(font, glyf + s) >= 0) return;
                int p = glyf + s + 10;
                while (true)
                {
                    int flags = U16(font, p);
                    Visit(U16(font, p + 2));
                    p += 4 + ((flags & 1) != 0 ? 4 : 2) + ((flags & 8) != 0 ? 2 : (flags & 0x40) != 0 ? 4 : (flags & 0x80) != 0 ? 8 : 0);
                    if ((flags & 0x20) == 0) break;
                }
            }
            Visit(0);
            foreach (var (_, gid) in map) Visit(gid);

            var newGlyf = new MemoryStream();
            var newLoca = new byte[(glyphs + 1) * 4];
            for (int g = 0; g < glyphs; g++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(4 * g), (uint)newGlyf.Length);
                if (!keep.Contains(g)) continue;
                int s = At(g), e = At(g + 1);
                newGlyf.Write(font, glyf + s, e - s);
                while (newGlyf.Length % 4 != 0) newGlyf.WriteByte(0);
            }
            BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(4 * glyphs), (uint)newGlyf.Length);

            var output = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var tag in Copied)
                if (tables.TryGetValue(tag, out var t)) output[tag] = font.AsSpan(t.Off, t.Len).ToArray();
            BinaryPrimitives.WriteInt16BigEndian(output["head"].AsSpan(50), 1); // длинный loca
            BinaryPrimitives.WriteUInt32BigEndian(output["head"].AsSpan(8), 0);
            output["loca"] = newLoca;
            output["glyf"] = newGlyf.ToArray();
            output["cmap"] = Cmap(map);
            if (tables.TryGetValue("post", out var post) && post.Len >= 32)
            {
                var p3 = font.AsSpan(post.Off, 32).ToArray(); // версия 3: без имён глифов
                BinaryPrimitives.WriteUInt32BigEndian(p3, 0x00030000);
                output["post"] = p3;
            }
            return Assemble(output);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException) { return null; }
    }

    /// <summary>cmap из двух подтаблиц: (3,1) формат 4 для BMP и (3,10) формат 12 для всех знаков; по сегменту на знак.</summary>
    static byte[] Cmap(IReadOnlyList<(int Cp, ushort Gid)> map)
    {
        var all = map.Where(m => m.Gid != 0).DistinctBy(m => m.Cp).OrderBy(m => m.Cp).ToList();
        var bmp = all.Where(m => m.Cp <= 0xFFFF).ToList();
        int seg = bmp.Count + 1, f4Len = 16 + seg * 8, f12Len = 16 + 12 * all.Count;
        var b = new byte[4 + 16 + f4Len + f12Len];
        var s = b.AsSpan();
        W16(s, 2, 2);
        W16(s, 4, 3); W16(s, 6, 1); W32(s, 8, 20);
        W16(s, 12, 3); W16(s, 14, 10); W32(s, 16, (uint)(20 + f4Len));

        var f4 = s[20..];
        int range = 2 * (1 << (int)Math.Log2(seg));
        W16(f4, 0, 4); W16(f4, 2, f4Len); W16(f4, 6, seg * 2); W16(f4, 8, range); W16(f4, 10, (int)Math.Log2(range / 2)); W16(f4, 12, seg * 2 - range);
        int end = 14, startCodes = end + seg * 2 + 2, deltas = startCodes + seg * 2;
        for (int i = 0; i < seg; i++)
        {
            int cp = i < bmp.Count ? bmp[i].Cp : 0xFFFF, gid = i < bmp.Count ? bmp[i].Gid : 0;
            W16(f4, end + 2 * i, cp);
            W16(f4, startCodes + 2 * i, cp);
            W16(f4, deltas + 2 * i, i < bmp.Count ? (gid - cp) & 0xFFFF : 1);
        }

        var f12 = s[(20 + f4Len)..];
        W16(f12, 0, 12); W32(f12, 4, (uint)f12Len); W32(f12, 12, (uint)all.Count);
        for (int i = 0; i < all.Count; i++)
        {
            W32(f12, 16 + 12 * i, (uint)all[i].Cp);
            W32(f12, 20 + 12 * i, (uint)all[i].Cp);
            W32(f12, 24 + 12 * i, all[i].Gid);
        }
        return b;
    }

    static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        int n = tables.Count, pow = 1 << (int)Math.Log2(n);
        int offset = 12 + 16 * n;
        int total = offset + tables.Values.Sum(t => (t.Length + 3) & ~3);
        var b = new byte[total];
        var s = b.AsSpan();
        W32(s, 0, 0x00010000);
        W16(s, 4, n); W16(s, 6, pow * 16); W16(s, 8, (int)Math.Log2(pow)); W16(s, 10, n * 16 - pow * 16);
        int i = 0, headAt = 0;
        foreach (var (tag, data) in tables)
        {
            int rec = 12 + 16 * i++;
            for (int k = 0; k < 4; k++) b[rec + k] = (byte)tag[k];
            data.CopyTo(b, offset);
            W32(s, rec + 4, Sum(b, offset, (data.Length + 3) & ~3));
            W32(s, rec + 8, (uint)offset);
            W32(s, rec + 12, (uint)data.Length);
            if (tag == "head") headAt = offset;
            offset += (data.Length + 3) & ~3;
        }
        W32(s, headAt + 8, 0xB1B0AFBA - Sum(b, 0, b.Length));
        return b;
    }

    static uint Sum(byte[] b, int from, int len)
    {
        uint sum = 0;
        for (int i = from; i < from + len; i += 4) sum += BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(i));
        return sum;
    }

    static string Tag(byte[] b, int at) => System.Text.Encoding.ASCII.GetString(b, at, 4);
    static int U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));
    static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
    static void W16(Span<byte> s, int at, int v) => BinaryPrimitives.WriteUInt16BigEndian(s[at..], (ushort)v);
    static void W32(Span<byte> s, int at, uint v) => BinaryPrimitives.WriteUInt32BigEndian(s[at..], v);
}
