using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Leaflet.Core;

/// <summary>
/// Регистрация типов файлов Markdown (.md, .markdown, .mdown, .mkd) в HKCU (права администратора не нужны).
/// Выполняется автоматически при первом запуске (флаг <c>assoc</c> в settings.ini), а также обновляет путь к exe, если папку перенесли.
/// В Windows 10/11 при отсутствии чужого UserChoice программа сразу становится обработчиком по умолчанию;
/// если в системе уже закреплена другая программа через защищённый UserChoice, открыть системный выбор можно через <see cref="OpenDefaultSettings"/>.
/// </summary>
static class FileAssoc
{
    public const string ProgId = "Leaflet.Markdown", LegacyAppKey = "Leaflet.exe", AppName = "Leaflet";
    public const string Capabilities = @"Software\Leaflet\Capabilities";
    public const string Classes = @"Software\Classes", RegisteredApps = @"Software\RegisteredApplications";
    public const string SettingsKey = "assoc";
    public static readonly string[] Extensions = [".md", ".markdown", ".mdown", ".mkd"];

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, nint a, nint b);
    [DllImport("shlwapi.dll", ExactSpelling = true)]
    static extern unsafe int AssocQueryStringW(int flags, int str, char* pszAssoc, char* pszExtra, char* pszOut, ref uint pcchOut);

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "mviewer.exe");

    public static bool IsMarkdown(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        foreach (var ext in Extensions)
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static string StripExt(string path)
    {
        foreach (var ext in Extensions)
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return path[..^ext.Length];
        return path;
    }

    /// <summary>
    /// Автоматическая регистрация при первом запуске (и обновление пути к exe, если программу переместили).
    /// Возвращает <c>true</c>, если выполнена первичная регистрация.
    /// </summary>
    public static bool EnsureFirstRun(Settings settings, string exe)
    {
        if (settings.ReadOnly) return false;
        try
        {
            if (settings[SettingsKey] == null)
            {
                Register(exe);
                settings[SettingsKey] = "1";
                settings.Save();
                return true;
            }
            if (settings[SettingsKey] == "1" && !IsRegistered())
            {
                Register(exe);
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        return false;
    }

    /// <param name="classes">Куда писать классы; в тестах — отдельная ветка вместо <see cref="Classes"/> (остальные пути — так же).</param>
    public static void Register(string exe, string classes = Classes, string? regApps = RegisteredApps, string capabilities = Capabilities)
    {
        var hkcu = Registry.CurrentUser;
        string cmd = $"\"{exe}\" \"%1\"";
        string icon = $"\"{exe}\",0";
        string appKey = Path.GetFileName(exe);
        if (string.IsNullOrEmpty(appKey)) appKey = "mviewer.exe";

        using (var k = hkcu.CreateSubKey($@"{classes}\{ProgId}"))
        {
            k.SetValue(null, Loc.T("assoc.progid"));
            k.SetValue("FriendlyTypeName", Loc.T("assoc.progid"));
        }
        SetDefault(hkcu, $@"{classes}\{ProgId}\DefaultIcon", icon);
        using (var k = hkcu.CreateSubKey($@"{classes}\{ProgId}\shell\open"))
        {
            k.SetValue(null, Loc.T("assoc.open"));
            k.SetValue("FriendlyAppName", AppName);
        }
        SetDefault(hkcu, $@"{classes}\{ProgId}\shell\open\command", cmd);

        if (!appKey.Equals(LegacyAppKey, StringComparison.OrdinalIgnoreCase))
            hkcu.DeleteSubKeyTree($@"{classes}\Applications\{LegacyAppKey}", false);

        string app = $@"{classes}\Applications\{appKey}";
        using (var k = hkcu.CreateSubKey(app)) k.SetValue("FriendlyAppName", AppName);
        SetDefault(hkcu, $@"{app}\DefaultIcon", icon);
        SetDefault(hkcu, $@"{app}\shell\open\command", cmd);
        using (var k = hkcu.CreateSubKey($@"{app}\SupportedTypes"))
            foreach (var ext in Extensions) k.SetValue(ext, "");

        foreach (var ext in Extensions)
        {
            using (var k = hkcu.CreateSubKey($@"{classes}\{ext}"))
            {
                k.SetValue(null, ProgId);
                k.SetValue("Content Type", "text/markdown");
                k.SetValue("PerceivedType", "text");
            }
            using var ow = hkcu.CreateSubKey($@"{classes}\{ext}\OpenWithProgids");
            ow.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        // Список «Приложения по умолчанию»: программа появляется в выборе для .md / .markdown.
        using (var k = hkcu.CreateSubKey(capabilities))
        {
            k.SetValue("ApplicationName", AppName);
            k.SetValue("ApplicationDescription", Loc.T("assoc.desc"));
            k.SetValue("ApplicationIcon", icon);
            using var fa = k.CreateSubKey("FileAssociations");
            foreach (var ext in Extensions) fa.SetValue(ext, ProgId);
        }
        if (regApps != null) { using var k = hkcu.CreateSubKey(regApps); k.SetValue(AppName, capabilities); }
        Notify(classes);
    }

    public static void Unregister(string classes = Classes, string? regApps = RegisteredApps, string capabilities = Capabilities)
    {
        var hkcu = Registry.CurrentUser;
        string appKey = Path.GetFileName(ExePath);
        hkcu.DeleteSubKeyTree($@"{classes}\{ProgId}", false);
        if (!string.IsNullOrEmpty(appKey))
            hkcu.DeleteSubKeyTree($@"{classes}\Applications\{appKey}", false);
        hkcu.DeleteSubKeyTree($@"{classes}\Applications\{LegacyAppKey}", false);

        foreach (var ext in Extensions)
        {
            using (var k = hkcu.OpenSubKey($@"{classes}\{ext}", true))
            {
                if (k?.GetValue(null) is string cur && cur == ProgId)
                    k.DeleteValue("", false);
            }
            using (var ow = hkcu.OpenSubKey($@"{classes}\{ext}\OpenWithProgids", true))
                ow?.DeleteValue(ProgId, false);
        }
        hkcu.DeleteSubKeyTree(capabilities, false);
        // Пустой родитель Software\Leaflet убираем.
        string parent = capabilities[..capabilities.LastIndexOf('\\')];
        using (var leaf = hkcu.OpenSubKey(parent))
        {
            if (leaf is { SubKeyCount: 0, ValueCount: 0 }) { leaf.Dispose(); hkcu.DeleteSubKey(parent, false); }
        }
        if (regApps != null) using (var k = hkcu.OpenSubKey(regApps, true)) k?.DeleteValue(AppName, false);
        Notify(classes);
    }

    public static bool IsRegistered(string classes = Classes)
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"{classes}\{ProgId}\shell\open\command");
        return k?.GetValue(null) is string s && s.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Проверяет через оболочку Windows, открывается ли сейчас <paramref name="ext"/> именно этим исполняемым файлом.</summary>
    public static unsafe bool IsDefault(string ext = ".md")
    {
        const int ASSOCSTR_EXECUTABLE = 2;
        uint cch = 1024;
        char* buf = stackalloc char[(int)cch];
        fixed (char* pExt = ext, pOpen = "open")
        {
            if (AssocQueryStringW(0, ASSOCSTR_EXECUTABLE, pExt, pOpen, buf, ref cch) != 0 || cch <= 1)
                return false;
        }
        string resolved = new string(buf, 0, (int)cch - 1).Trim().Trim('\0', '"');
        if (resolved.Length == 0) return false;
        try
        {
            return string.Equals(Path.GetFullPath(resolved), Path.GetFullPath(ExePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return string.Equals(resolved, ExePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Открывает страницу приложения в «Параметры Windows → Приложения по умолчанию» для подтверждения выбора в 1 клик.</summary>
    public static void OpenDefaultSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo($"ms-settings:defaultapps?registeredAppUser={Uri.EscapeDataString(AppName)}") { UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); } catch { }
        }
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
