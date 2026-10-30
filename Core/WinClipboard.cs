using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

namespace Leaflet.Core;

/// <summary>
/// Буфер обмена Windows напрямую: картинка (формат «PNG» как есть, иначе CF_DIB → PNG) и файлы из проводника (CF_HDROP).
/// Синхронно и без пересжатия через Avalonia; текст по-прежнему вставляет редактор.
/// </summary>
public static class WinClipboard
{
    const uint CF_DIB = 8, CF_UNICODETEXT = 13, CF_HDROP = 15, GMEM_MOVEABLE = 2;
    static readonly uint CF_PNG = RegisterClipboardFormatW("PNG");

    public static bool HasText => IsClipboardFormatAvailable(CF_UNICODETEXT);
    public static bool HasFiles => IsClipboardFormatAvailable(CF_HDROP);
    public static bool HasImage => IsClipboardFormatAvailable(CF_PNG) || IsClipboardFormatAvailable(CF_DIB);

    public static string[] Files()
    {
        if (!HasFiles || !Open(0)) return [];
        try
        {
            nint h = GetClipboardData(CF_HDROP);
            if (h == 0) return [];
            uint count = DragQueryFileW(h, 0xFFFFFFFF, null, 0);
            var files = new string[count];
            for (uint i = 0; i < count; i++)
            {
                var sb = new StringBuilder((int)DragQueryFileW(h, i, null, 0) + 1);
                DragQueryFileW(h, i, sb, sb.Capacity);
                files[i] = sb.ToString();
            }
            return files;
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Картинка из буфера в PNG или null.</summary>
    public static byte[]? ImagePng()
    {
        if (!HasImage || !Open(0)) return null;
        try
        {
            if (Bytes(CF_PNG) is { Length: > 8 } png && png[0] == 0x89 && png[1] == (byte)'P') return png;
            return Bytes(CF_DIB) is { } dib ? DibToPng(dib) : null;
        }
        finally { CloseClipboard(); }
    }

    static byte[]? Bytes(uint format)
    {
        if (!IsClipboardFormatAvailable(format)) return null;
        nint h = GetClipboardData(format);
        if (h == 0) return null;
        nint p = GlobalLock(h);
        if (p == 0) return null;
        try
        {
            var data = new byte[(int)GlobalSize(h)];
            Marshal.Copy(p, data, 0, data.Length);
            return data;
        }
        finally { GlobalUnlock(h); }
    }

    /// <summary>
    /// DIB (BITMAPINFOHEADER + пиксели) → PNG. 24 и 32 бита разбираем сами: у снимков экрана альфа в DIB обычно нулевая,
    /// поэтому картинка считается непрозрачной (прозрачные приложения кладут ещё и «PNG», он берётся раньше). Остальное — через BMP в Skia.
    /// </summary>
    public static byte[]? DibToPng(byte[] dib)
    {
        if (dib.Length < 40) return null;
        int headerSize = BitConverter.ToInt32(dib, 0), width = BitConverter.ToInt32(dib, 4), height = BitConverter.ToInt32(dib, 8);
        int bpp = BitConverter.ToUInt16(dib, 14), compression = BitConverter.ToInt32(dib, 16), colors = BitConverter.ToInt32(dib, 32);
        if (width <= 0 || height == 0 || width > 30000 || Math.Abs(height) > 30000) return null;
        int masks = compression == 3 && headerSize == 40 ? 12 : 0;
        int palette = (colors == 0 && bpp <= 8 ? 1 << bpp : colors) * 4;
        int offset = headerSize + masks + palette;
        bool topDown = height < 0;
        int h = Math.Abs(height), stride = (width * bpp + 31) / 32 * 4;

        SKBitmap? bmp = null;
        try
        {
            if (bpp is 24 or 32 && compression is 0 or 3 && offset + (long)stride * h <= dib.Length)
            {
                bmp = new SKBitmap(new SKImageInfo(width, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
                var px = new byte[width * 4 * h];
                for (int y = 0; y < h; y++)
                {
                    int src = offset + (topDown ? y : h - 1 - y) * stride, dst = y * width * 4;
                    for (int x = 0; x < width; x++, dst += 4)
                    {
                        int s = src + x * (bpp / 8);
                        px[dst] = dib[s]; px[dst + 1] = dib[s + 1]; px[dst + 2] = dib[s + 2]; px[dst + 3] = 255;
                    }
                }
                Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
            }
            else
            {
                var file = new byte[14 + dib.Length];
                file[0] = (byte)'B'; file[1] = (byte)'M';
                BitConverter.GetBytes(file.Length).CopyTo(file, 2);
                BitConverter.GetBytes(14 + offset).CopyTo(file, 10);
                dib.CopyTo(file, 14);
                bmp = SKBitmap.Decode(file);
                if (bmp == null) return null;
            }
            using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
            return data?.ToArray();
        }
        finally { bmp?.Dispose(); }
    }

    // ---- запись (для самопроверки) ----

    /// <summary>Кладёт в буфер картинку как CF_DIB (32 бита, снизу вверх) — так её кладёт «Ножницы» и PrintScreen.</summary>
    public static bool SetDib(nint owner, int width, int height, Func<int, int, uint> argb)
    {
        int stride = width * 4;
        var dib = new byte[40 + stride * height];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(width).CopyTo(dib, 4);
        BitConverter.GetBytes(height).CopyTo(dib, 8);
        BitConverter.GetBytes((ushort)1).CopyTo(dib, 12);
        BitConverter.GetBytes((ushort)32).CopyTo(dib, 14);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                BitConverter.GetBytes(argb(x, y) & 0x00ffffff).CopyTo(dib, 40 + (height - 1 - y) * stride + x * 4);
        return Set(owner, (CF_DIB, dib));
    }

    public static bool SetFiles(nint owner, IEnumerable<string> files)
    {
        var names = Encoding.Unicode.GetBytes(string.Concat(files.Select(f => f + "\0")) + "\0");
        var data = new byte[20 + names.Length];
        BitConverter.GetBytes(20).CopyTo(data, 0);   // DROPFILES.pFiles
        BitConverter.GetBytes(1).CopyTo(data, 16);   // fWide
        names.CopyTo(data, 20);
        return Set(owner, (CF_HDROP, data));
    }

    public static bool SetText(nint owner, string text) => Set(owner, (CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0")));

    public static string? Text()
    {
        if (!HasText || !Open(0)) return null;
        try { return Bytes(CF_UNICODETEXT) is { } b ? Encoding.Unicode.GetString(b).Split('\0')[0] : null; }
        finally { CloseClipboard(); }
    }

    static bool Set(nint owner, params (uint Format, byte[] Data)[] items)
    {
        if (!Open(owner)) return false;
        try
        {
            EmptyClipboard();
            foreach (var (format, data) in items)
            {
                nint h = GlobalAlloc(GMEM_MOVEABLE, data.Length);
                nint p = GlobalLock(h);
                Marshal.Copy(data, 0, p, data.Length);
                GlobalUnlock(h);
                if (SetClipboardData(format, h) == 0) return false;
            }
            return true;
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Буфер бывает занят другим приложением — несколько попыток.</summary>
    static bool Open(nint owner)
    {
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(owner)) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] static extern nint SetClipboardData(uint format, nint mem);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormatW(string name);
    [DllImport("kernel32.dll")] static extern nint GlobalLock(nint mem);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint mem);
    [DllImport("kernel32.dll")] static extern nuint GlobalSize(nint mem);
    [DllImport("kernel32.dll")] static extern nint GlobalAlloc(uint flags, nint bytes);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFileW(nint drop, uint index, StringBuilder? file, int size);
}
