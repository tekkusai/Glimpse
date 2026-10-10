using System.Windows;
using System.Windows.Threading;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// Tests that build real windows share one STA thread and one WPF
/// Application for the whole run, merged with the app's own theme
/// dictionaries, so header layout is measured on the real arranged tree.
/// The collection runs alone: it moves the process-wide palette.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfCollection
{
    public const string Name = "WPF application";
}

internal static class WpfApp
{
    private static readonly Lazy<Dispatcher> Host = new(Start);

    /// <summary>Runs <paramref name="body"/> on the app's thread; awaits inside it keep the dispatcher pumping.</summary>
    public static Task RunAsync(Func<Task> body) =>
        Host.Value.InvokeAsync(body).Task.Unwrap();

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (var name in new[] { "Scrollbars", "Menus", "Chrome" })
                {
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/Glimpse;component/Themes/{name}.xaml")
                    });
                }

                Theme.Use(ThemePreference.Dark);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                ready.Set();
            }

            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF test application"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (failure is not null)
        {
            throw new InvalidOperationException("The WPF test application failed to start.", failure);
        }

        return dispatcher!;
    }
}
