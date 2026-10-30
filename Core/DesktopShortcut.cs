using System.Runtime.InteropServices;

namespace Leaflet.Core;

/// <summary>
/// Ярлык на рабочем столе при самом первом запуске: один раз (флаг <c>shortcut</c> в settings.ini),
/// не из папки сборки (в поставке рядом с exe лежит portable.txt, ярлык создаётся и там: он только указывает на exe). Удалённый пользователем ярлык заново не появляется.
/// </summary>
static class DesktopShortcut
{
    public const string Key = "shortcut";

    static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    static readonly Guid IidShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    static readonly Guid IidPersistFile = new("0000010b-0000-0000-C000-000000000046");

    [DllImport("ole32.dll")] static extern int CoInitializeEx(nint pvReserved, uint dwCoInit);
    [DllImport("ole32.dll")] static extern void CoUninitialize();
    [DllImport("ole32.dll")] static extern int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

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

    public static unsafe bool Create(string exe, string lnkPath)
    {
        int hrInit = CoInitializeEx(0, 2 /* COINIT_APARTMENTTHREADED */);
        bool uninit = hrInit is 0 or 1; // S_OK или S_FALSE
        nint link = 0, persist = 0;
        try
        {
            if (CoCreateInstance(in ClsidShellLink, 0, 1 /* CLSCTX_INPROC_SERVER */, in IidShellLinkW, out link) != 0 || link == 0)
                return false;

            nint* vtbl = *(nint**)link;
            var queryInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtbl[0];
            var setDesc = (delegate* unmanaged[Stdcall]<nint, char*, int>)vtbl[7];
            var setWorkDir = (delegate* unmanaged[Stdcall]<nint, char*, int>)vtbl[9];
            var setIcon = (delegate* unmanaged[Stdcall]<nint, char*, int, int>)vtbl[17];
            var setPath = (delegate* unmanaged[Stdcall]<nint, char*, int>)vtbl[20];

            string workDir = Path.GetDirectoryName(exe) ?? "";
            fixed (char* pExe = exe, pWorkDir = workDir, pDesc = "Leaflet", pLnk = lnkPath)
            {
                if (setPath(link, pExe) < 0) return false;
                if (setWorkDir(link, pWorkDir) < 0) return false;
                if (setIcon(link, pExe, 0) < 0) return false;
                if (setDesc(link, pDesc) < 0) return false;

                Guid iid = IidPersistFile;
                nint pf;
                if (queryInterface(link, &iid, &pf) < 0 || pf == 0) return false;
                persist = pf;

                var save = (delegate* unmanaged[Stdcall]<nint, char*, int, int>)(*(nint**)persist)[6];
                if (save(persist, pLnk, 1) < 0) return false;
            }
            return File.Exists(lnkPath);
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (persist != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)persist)[2])(persist);
            if (link != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)link)[2])(link);
            if (uninit) CoUninitialize();
        }
    }
}
