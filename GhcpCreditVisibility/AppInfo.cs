using System.Reflection;

namespace GhcpCreditVisibility
{
    /// <summary>Application metadata surfaced in the UI (e.g. the footer version stamp).</summary>
    public static class AppInfo
    {
        /// <summary>Semantic version from the assembly (set via &lt;Version&gt; in the .csproj).</summary>
        public static string Version { get; } =
            Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
            ?? "1.0.0";
    }
}
