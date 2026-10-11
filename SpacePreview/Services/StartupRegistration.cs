using Microsoft.Win32;
using System.Diagnostics;
using SpacePreview.Utilities;

namespace SpacePreview.Services;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string CurrentExecutablePath => Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? throw new InvalidOperationException("Could not resolve the current executable path.");

    public static void Install()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key?.SetValue(Branding.Name, Quote(CurrentExecutablePath), RegistryValueKind.String);
    }

    public static void Uninstall()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(Branding.Name, throwOnMissingValue: false);
    }

    public static bool IsInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(Branding.Name) is string value && value.Contains(CurrentExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string path)
    {
        return path.Contains(' ') ? $"\"{path}\"" : path;
    }
}
