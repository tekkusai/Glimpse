using System.Runtime.CompilerServices;

namespace SpacePreview.Tests;

/// <summary>
/// Points the whole test run, and every helper process it spawns, at a
/// throwaway data root before any product type reads one. Without it the
/// suite wrote into the user's real cache and diagnostic log, the very log
/// field evidence is drawn from.
/// </summary>
internal static class TestDataRoot
{
    public static string Path { get; private set; } = string.Empty;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "GlimpseTests",
            $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", Path);
    }
}
