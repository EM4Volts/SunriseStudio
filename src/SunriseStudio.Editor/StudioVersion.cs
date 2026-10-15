using System.Reflection;

namespace SunriseStudio.Editor;

/** The release version every Studio assembly is built with. */
public static class StudioVersion
{
    public static string Text { get; } = typeof(StudioVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}
