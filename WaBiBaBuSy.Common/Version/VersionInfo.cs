using System.Reflection;

namespace WaBiBaBuSy.Common.Version;

/// <summary>
/// Provides version information about the application
/// </summary>
public static class VersionInfo
{
    private static readonly Lazy<System.Version> _version = new(() =>
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return assembly.GetName().Version ?? new System.Version(2, 0, 0, 1);
    });

    private static readonly Lazy<string> _informationalVersion = new(() =>
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return attribute?.InformationalVersion ?? $"{_version.Value.Major}.{_version.Value.Minor}.{_version.Value.Build}";
    });

    /// <summary>
    /// Gets the current application version (e.g., "2.0.0")
    /// </summary>
    public static string AppVersion => $"{_version.Value.Major}.{_version.Value.Minor}.{_version.Value.Build}";

    /// <summary>
    /// Gets the current build number (revision component of assembly version)
    /// </summary>
    public static int BuildNumber => _version.Value.Revision;

    /// <summary>
    /// Gets the target framework version (e.g., "net8.0")
    /// </summary>
    public static string FrameworkVersion
    {
        get
        {
            var frameworkName = AppDomain.CurrentDomain.SetupInformation.TargetFrameworkName;
            if (frameworkName?.Contains(".NETCoreApp,Version=v8.0") == true)
                return "net8.0";
            return frameworkName ?? "unknown";
        }
    }

    /// <summary>
    /// Gets the full informational version string (includes build metadata)
    /// </summary>
    public static string InformationalVersion => _informationalVersion.Value;

    /// <summary>
    /// Compares two version strings
    /// </summary>
    /// <param name="version1">First version string (e.g., "2.1.0")</param>
    /// <param name="version2">Second version string (e.g., "2.0.0")</param>
    /// <returns>
    /// -1 if version1 &lt; version2,
    ///  0 if version1 == version2,
    ///  1 if version1 &gt; version2
    /// </returns>
    public static int CompareVersions(string version1, string version2)
    {
        if (!System.Version.TryParse(version1, out var v1))
            throw new ArgumentException($"Invalid version format: {version1}", nameof(version1));

        if (!System.Version.TryParse(version2, out var v2))
            throw new ArgumentException($"Invalid version format: {version2}", nameof(version2));

        return v1.CompareTo(v2);
    }

    /// <summary>
    /// Compares version and build number combination
    /// </summary>
    /// <param name="version1">First version</param>
    /// <param name="build1">First build number</param>
    /// <param name="version2">Second version</param>
    /// <param name="build2">Second build number</param>
    /// <returns>
    /// -1 if (version1, build1) &lt; (version2, build2),
    ///  0 if equal,
    ///  1 if greater
    /// </returns>
    public static int CompareVersionsWithBuild(string version1, int build1, string version2, int build2)
    {
        var versionCompare = CompareVersions(version1, version2);
        if (versionCompare != 0)
            return versionCompare;

        // Versions are equal, compare build numbers
        return build1.CompareTo(build2);
    }

    /// <summary>
    /// Checks if an update is required based on minimum compatible version
    /// </summary>
    /// <param name="currentVersion">Current version</param>
    /// <param name="minimumVersion">Minimum required version</param>
    /// <returns>True if current version is below minimum, false otherwise</returns>
    public static bool IsUpdateRequired(string currentVersion, string minimumVersion)
    {
        return CompareVersions(currentVersion, minimumVersion) < 0;
    }

    /// <summary>
    /// Checks if a newer version is available
    /// </summary>
    /// <param name="currentVersion">Current version</param>
    /// <param name="currentBuild">Current build number</param>
    /// <param name="availableVersion">Available version</param>
    /// <param name="availableBuild">Available build number</param>
    /// <returns>True if available version is newer</returns>
    public static bool IsNewerVersion(string currentVersion, int currentBuild,
                                       string availableVersion, int availableBuild)
    {
        return CompareVersionsWithBuild(currentVersion, currentBuild,
                                        availableVersion, availableBuild) < 0;
    }
}
