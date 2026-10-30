using System.Reflection;

namespace Leaflet.Core;

static class AppVersion
{
    public static string Number { get; } = (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0").Split('+')[0];
    public static string Text => "Leaflet " + Number;
}
