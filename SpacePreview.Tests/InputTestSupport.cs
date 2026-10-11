using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SpacePreview.Tests;

/// <summary>
/// Runs WPF work on one STA thread that hosts an Application carrying the
/// app's own theme dictionaries, the resources the preview window looks up
/// when it is built. A failure is rethrown on the calling thread.
/// </summary>
internal static class Sta
{
    private static readonly Lazy<Dispatcher> Ui = new(StartUiThread);

    public static void Run(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        Ui.Value.Invoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        failure?.Throw();
    }

    private static Dispatcher StartUiThread()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var source in new[] { "Themes/Scrollbars.xaml", "Themes/Menus.xaml", "Themes/Chrome.xaml" })
            {
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/Glimpse;component/{source}")
                });
            }

            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Input tests UI"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }
}

/// <summary>Reads and writes private state the input tests set up or check.</summary>
internal static class Fields
{
    public static void Set(object target, string name, object? value) =>
        Find(target.GetType(), name).SetValue(target, value);

    public static T Get<T>(object target, string name) =>
        (T)Find(target.GetType(), name).GetValue(target)!;

    public static object? Call(object target, string method, params object?[] args) =>
        target.GetType()
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .Invoke(target, args);

    private static FieldInfo Find(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var field = current.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?? current.GetField($"<{name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field is not null)
            {
                return field;
            }
        }

        throw new MissingFieldException(type.Name, name);
    }
}

/// <summary>Raises a key on an element the way the input system routes it, with no window shown.</summary>
internal static class Keys
{
    public static KeyEventArgs PreviewKeyDown(UIElement element, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, new DetachedSource(), 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        element.RaiseEvent(args);
        return args;
    }

    private sealed class DetachedSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }

        public override bool IsDisposed => false;

        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
}
