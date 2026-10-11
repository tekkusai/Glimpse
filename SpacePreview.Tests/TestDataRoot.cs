using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SpacePreview.Tests;

/// <summary>
/// Points the whole test run, and every helper process it spawns, at a
/// throwaway data root before any product type reads one. Without it the
/// suite wrote into the user's real cache and diagnostic log, the very log
/// field evidence is drawn from. The data root and every test's scratch
/// folder sit under one folder per run, which goes when the run ends; a
/// run that was killed first is swept by the next one.
/// </summary>
internal static class TestDataRoot
{
    public static string Path { get; private set; } = string.Empty;

    /// <summary>This run's folder: the data root, and scratch beside it.</summary>
    public static string RunRoot { get; private set; } = string.Empty;

    [ModuleInitializer]
    internal static void Initialize()
    {
        var runs = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GlimpseTests");
        SweepEndedRuns(runs);
        RunRoot = System.IO.Path.Combine(runs, $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        Path = System.IO.Path.Combine(RunRoot, "data");
        Directory.CreateDirectory(Path);
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", Path);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(RunRoot);
    }

    /// <summary>Runs are named for their process; one whose process is gone left its folder behind.</summary>
    private static void SweepEndedRuns(string runs)
    {
        if (!Directory.Exists(runs))
        {
            return;
        }

        foreach (var run in Directory.EnumerateDirectories(runs))
        {
            var name = System.IO.Path.GetFileName(run);
            var dash = name.IndexOf('-');
            // A day is longer than any run; past it, the id may be another process's.
            if (dash > 0
                && int.TryParse(name[..dash], out var processId)
                && (!IsRunning(processId) || Directory.GetCreationTimeUtc(run) < DateTime.UtcNow.AddDays(-1)))
            {
                TryDelete(run);
            }
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            // Can't tell: leave it for a later run.
            return true;
        }
    }

    /// <summary>Best effort: a file a helper still holds stays until the next sweep.</summary>
    internal static void TryDelete(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(folder, recursive: true);
        }
        catch
        {
        }
    }
}
