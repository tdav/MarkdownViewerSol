using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace Leaflet.Core;

/// <summary>
/// «Открыть с помощью» для .md: запись в HKCU (права администратора не нужны), только по команде.
/// Программой по умолчанию Leaflet не становится — Windows оставляет этот выбор пользователю
/// (он увидит Leaflet в «Открыть с помощью» и в «Параметры → Приложения по умолчанию»).
/// </summary>
static class FileAssoc
{
    public const string ProgId = "Leaflet.Markdown", AppKey = "Leaflet.exe", Capabilities = @"Software\Leaflet\Capabilities";
    public const string Classes = @"Software\Classes", RegisteredApps = @"Software\RegisteredApplications";
    static readonly string[] Extensions = [".md"];

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, nint a, nint b);

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Leaflet.exe");

    /// <param name="classes">Куда писать классы; в тестах — отдельная ветка вместо <see cref="Classes"/> (остальные пути — так же).</param>
    public static void Register(string exe, string classes = Classes, string? regApps = RegisteredApps, string capabilities = Capabilities)
    {
        var hkcu = Registry.CurrentUser;
        string cmd = $"\"{exe}\" \"%1\"";
        SetDefault(hkcu, $@"{classes}\{ProgId}", Loc.T("assoc.progid"));
        SetDefault(hkcu, $@"{classes}\{ProgId}\DefaultIcon", $"\"{exe}\",0");
        SetDefault(hkcu, $@"{classes}\{ProgId}\shell\open", Loc.T("assoc.open"));
        SetDefault(hkcu, $@"{classes}\{ProgId}\shell\open\command", cmd);

        string app = $@"{classes}\Applications\{AppKey}";
        using (var k = hkcu.CreateSubKey(app)) k.SetValue("FriendlyAppName", "Leaflet");
        SetDefault(hkcu, $@"{app}\DefaultIcon", $"\"{exe}\",0");
        SetDefault(hkcu, $@"{app}\shell\open\command", cmd);
        using (var k = hkcu.CreateSubKey($@"{app}\SupportedTypes"))
            foreach (var ext in Extensions) k.SetValue(ext, "");

        foreach (var ext in Extensions)
        {
            using var k = hkcu.CreateSubKey($@"{classes}\{ext}\OpenWithProgids");
            k.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        // Список «Приложения по умолчанию»: Leaflet появляется в выборе для .md.
        using (var k = hkcu.CreateSubKey(capabilities))
        {
            k.SetValue("ApplicationName", "Leaflet");
            k.SetValue("ApplicationDescription", Loc.T("assoc.desc"));
            using var fa = k.CreateSubKey("FileAssociations");
            foreach (var ext in Extensions) fa.SetValue(ext, ProgId);
        }
        if (regApps != null) { using var k = hkcu.CreateSubKey(regApps); k.SetValue("Leaflet", capabilities); }
        Notify(classes);
    }

    public static void Unregister(string classes = Classes, string? regApps = RegisteredApps, string capabilities = Capabilities)
    {
        var hkcu = Registry.CurrentUser;
        hkcu.DeleteSubKeyTree($@"{classes}\{ProgId}", false);
        hkcu.DeleteSubKeyTree($@"{classes}\Applications\{AppKey}", false);
        foreach (var ext in Extensions)
            using (var k = hkcu.OpenSubKey($@"{classes}\{ext}\OpenWithProgids", true)) k?.DeleteValue(ProgId, false);
        hkcu.DeleteSubKeyTree(capabilities, false);
        // Пустой родитель Software\Leaflet убираем.
        string parent = capabilities[..capabilities.LastIndexOf('\\')];
        using (var leaf = hkcu.OpenSubKey(parent))
        {
            if (leaf is { SubKeyCount: 0, ValueCount: 0 }) { leaf.Dispose(); hkcu.DeleteSubKey(parent, false); }
        }
        if (regApps != null) using (var k = hkcu.OpenSubKey(regApps, true)) k?.DeleteValue("Leaflet", false);
        Notify(classes);
    }

    public static bool IsRegistered(string classes = Classes)
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"{classes}\{ProgId}\shell\open\command");
        return k?.GetValue(null) is string s && s.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
    }

    static void SetDefault(RegistryKey root, string path, string value)
    {
        using var k = root.CreateSubKey(path);
        k.SetValue(null, value);
    }

    static void Notify(string classes)
    {
        if (classes == Classes) SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, 0, 0);
    }
}
