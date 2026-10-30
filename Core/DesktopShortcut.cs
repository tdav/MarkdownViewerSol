using System.Runtime.InteropServices;

namespace Leaflet.Core;

/// <summary>
/// Ярлык на рабочем столе при самом первом запуске: один раз (флаг <c>shortcut</c> в settings.ini),
/// не из папки сборки (в поставке рядом с exe лежит portable.txt, ярлык создаётся и там: он только указывает на exe). Удалённый пользователем ярлык заново не появляется.
/// </summary>
static class DesktopShortcut
{
    public const string Key = "shortcut";

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Leaflet.lnk");

    /// <summary>Создаёт ярлык, если это первый запуск обычной установленной копии; true — ярлык создан сейчас.</summary>
    public static bool EnsureFirstRun(Settings settings, string exe, string? lnkPath = null)
    {
        if (settings[Key] != null || IsBuildFolder(exe)) return false;
        settings[Key] = "1";
        settings.Save();
        string lnk = lnkPath ?? DefaultPath;
        if (File.Exists(lnk)) return false;
        return Create(exe, lnk);
    }

    static bool IsBuildFolder(string exe)
    {
        string dir = (Path.GetDirectoryName(exe) ?? "") + "\\";
        return dir.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) || dir.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);
    }

    public static bool Create(string exe, string lnkPath)
    {
        object? shell = null, link = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return false;
            shell = Activator.CreateInstance(type);
            dynamic sh = shell!;
            link = sh.CreateShortcut(lnkPath);
            dynamic l = link!;
            l.TargetPath = exe;
            l.WorkingDirectory = Path.GetDirectoryName(exe) ?? "";
            l.IconLocation = exe + ",0";
            l.Description = "Leaflet";
            l.Save();
            return File.Exists(lnkPath);
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return false;
        }
        finally
        {
            if (link != null) Marshal.FinalReleaseComObject(link);
            if (shell != null) Marshal.FinalReleaseComObject(shell);
        }
    }
}
