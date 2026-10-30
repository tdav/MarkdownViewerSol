using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Leaflet.Core;

[assembly: SupportedOSPlatform("windows")]

namespace Leaflet;

static class Program
{
    public const string HelpUrl = "https://dalink.to/olegche";
    public static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static string[] Args { get; private set; } = [];

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);

    [STAThread]
    public static int Main(string[] args)
    {
        Args = args;
        if (args.Length >= 2 && args[0] == "--gen")
        {
            //SampleGenerator.Write(args[1], args.Length > 2 ? int.Parse(args[2]) : 5000);
            return 0;
        }
        int data = Array.IndexOf(args, "--data");
        if (data >= 0 && data + 1 < args.Length) Settings.UseDataDir(args[data + 1]);
        Loc.Init(StartLanguage(args));
        if (args.Contains("--register") || args.Contains("--unregister")) return Association(args);
        if (args.Contains("--version")) { Message(AppVersion.Text); return 0; }
        if (args.Contains("--help") || args.Contains("-h") || args.Contains("/?")) { Message(Usage); return 0; }
        if (SingleInstance.Applies(args) && SingleInstance.TryForward(args)) return 0;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Язык запуска: <c>--lang ru|en</c>; в замерах и самопроверке по умолчанию русский (строки проверок русские); иначе настройка или язык системы.</summary>
    internal static string StartLanguage(string[] args)
    {
        int i = Array.IndexOf(args, "--lang");
        if (i >= 0 && i + 1 < args.Length && args[i + 1] is Loc.Ru or Loc.En) return args[i + 1];
        if (args.Any(a => a is "--selftest" or "--bench" or "--fontcheck" or "--crashtest")) return Loc.Ru;
        return Loc.Detect(Settings.Load(readOnly: false)["lang"]);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    static string Usage => Loc.T("usage");
    static int Association(string[] args)
    {
        bool quiet = args.Contains("--quiet");
        try
        {
            if (args.Contains("--register"))
            {
                FileAssoc.Register(FileAssoc.ExePath);
                if (!quiet) Message(Loc.T("prog.registered"));
            }
            else
            {
                FileAssoc.Unregister();
                if (!quiet) Message(Loc.T("prog.unregistered"));
            }
            return 0;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            if (!quiet) Message(Loc.T("shell.reg.fail") + e.Message);
            return 1;
        }
    }

    static void Message(string text) => MessageBoxW(0, text, "Leaflet", 0x40 /* MB_ICONINFORMATION */);
}
