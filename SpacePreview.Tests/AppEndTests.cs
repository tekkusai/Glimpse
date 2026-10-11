using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using SpacePreview.Preview;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// However the app ends without asking (the installer's quit, Windows
/// ending the session, the last exit), placed work still waiting out its
/// pause is on disk before the process goes, and the log says so.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class AppEndTests
{
    [Theory]
    [InlineData("QuitForSetup", "installer quit")]
    [InlineData("OnSessionEnding", "session ending")]
    [InlineData("OnExit", "app exit")]
    public Task EveryEndOfTheAppKeepsWaitingWorkAsADraft(string end, string reason) => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var pdf = Path.Combine(temp.Path, $"{end}.pdf");
        File.WriteAllText(pdf, "%PDF-1.4 a contract");
        var keeper = new SignDraftKeeper(pdf, FileStamp.Of(pdf)!.Value, 0, []);
        try
        {
            keeper.Update(0, [new SignPlacement(SignPlacementKind.Date, 0, 0.4, 0.2, 0.14, 0.028, "10/10/2026")]);

            End(end);

            Assert.True(File.Exists(SignDrafts.PathFor(pdf)));
            Assert.True(await LogEventuallyContainsAsync($"{reason} kept unsaved signing work as a draft"));
        }
        finally
        {
            keeper.Discard();
        }
    });

    /// <summary>
    /// Runs one of the app's ends on an App that never started: this process
    /// already has its Application. Setup's quit ends in Shutdown, which WPF
    /// ignores while it already reports shutting down.
    /// </summary>
    private static void End(string end)
    {
        var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        var shuttingDown = typeof(Application).GetProperty("IsShuttingDown", BindingFlags.Static | BindingFlags.NonPublic)!;
        object[] args = end switch
        {
            "OnExit" => [Internal<ExitEventArgs>(0)],
            "OnSessionEnding" => [Internal<SessionEndingCancelEventArgs>(ReasonSessionEnding.Shutdown)],
            _ => []
        };

        shuttingDown.SetValue(null, true);
        try
        {
            Fields.Call(app, end, args);
        }
        finally
        {
            shuttingDown.SetValue(null, false);
        }
    }

    private static T Internal<T>(object argument) =>
        (T)Activator.CreateInstance(typeof(T), BindingFlags.Instance | BindingFlags.NonPublic, null, [argument], null)!;

    internal static async Task<bool> LogEventuallyContainsAsync(string text)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var stream = new FileStream(DiagnosticLog.Pathname, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                if (reader.ReadToEnd().Contains(text, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                // Not written yet.
            }

            await Task.Delay(50);
        }

        return false;
    }
}
