using System.Reflection;

namespace FileDetector.Services;

/// <summary>
/// The running build's version, read from the assembly rather than a hard-coded constant so there
/// is exactly one place (the csproj) that says what version this is.
/// </summary>
internal static class AppVersion
{
    /// <summary>e.g. <c>1.0.0</c> — the marketing version, without build metadata.</summary>
    public static string Display { get; } = Compute(out _);

    /// <summary>e.g. <c>1.0.0+3f2a1bc</c> — includes the source revision when the build supplied one.</summary>
    public static string Full { get; } = ComputeFull();

    private static string ComputeFull()
    {
        _ = Compute(out var full);
        return full;
    }

    private static string Compute(out string full)
    {
        var informational = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            // No attribute at all should not happen, but fall back rather than throwing at startup.
            var version = typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            full = version;
            return version;
        }

        full = informational;

        // MSBuild appends "+<SourceRevisionId>" when the build passes one; the leading part is the
        // version users care about.
        var plus = informational.IndexOf('+');
        return plus > 0 ? informational[..plus] : informational;
    }
}
