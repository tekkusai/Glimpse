using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SpacePreview.Tests;

/// <summary>
/// Runs WPF work on the test run's one WPF application (<see cref="WpfApp"/>).
/// A process holds one Application, so every STA test shares it.
/// </summary>
internal static class Sta
{
    public static void Run(Action action) =>
        WpfApp.RunAsync(() =>
        {
            action();
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();
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
