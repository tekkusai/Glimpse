using Microsoft.Win32;
using SpacePreview.Services;

namespace SpacePreview.Utilities;

/// <summary>
/// Reads and writes the same per-exe compatibility bits used by the
/// Properties > Compatibility dialog. User changes are stored under HKCU.
/// </summary>
internal static class ExecutableTweaks
{
    public const string RunAsAdminFlag = "RUNASADMIN";
    public const string DisableFullscreenOptimizationsFlag = "DISABLEDXMAXIMIZEDWINDOWEDMODE";

    private const string LayersKeyPath = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

    public static bool HasFlag(string exePath, string flag)
    {
        // The Properties dialog writes HKCU normally and HKLM when "for all
        // users" is ticked; awareness means reading both. Toggling only ever
        // writes HKCU (HKLM needs elevation).
        return HasFlagIn(Registry.CurrentUser, exePath, flag)
            || HasFlagIn(Registry.LocalMachine, exePath, flag);
    }

    /// <summary>
    /// True when the flag is set machine-wide (Properties "for all users").
    /// We cannot remove that without elevation, so the UI must show it as
    /// locked rather than pretend the switch works.
    /// </summary>
    public static bool IsMachineEnforced(string exePath, string flag)
    {
        return HasFlagIn(Registry.LocalMachine, exePath, flag);
    }

    private static bool HasFlagIn(RegistryKey hive, string exePath, string flag)
    {
        try
        {
            using var key = hive.OpenSubKey(LayersKeyPath);
            return ReadFlags(key?.GetValue(exePath) as string).Contains(flag, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Flips the flag and returns the new state.</summary>
    public static bool ToggleFlag(string exePath, string flag)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(LayersKeyPath);
            var flags = ReadFlags(key.GetValue(exePath) as string);
            bool enabled;
            if (flags.Contains(flag, StringComparer.OrdinalIgnoreCase))
            {
                flags.RemoveAll(f => f.Equals(flag, StringComparison.OrdinalIgnoreCase));
                enabled = false;
            }
            else
            {
                flags.Add(flag);
                enabled = true;
            }

            if (flags.Count == 0)
            {
                key.DeleteValue(exePath, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(exePath, "~ " + string.Join(' ', flags));
            }

            return enabled;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"compat flag toggle failed {flag} {exePath}: {ex.GetType().Name}: {ex.Message}");
            return HasFlag(exePath, flag);
        }
    }

    private static List<string> ReadFlags(string? value)
    {
        return value is null
            ? []
            : value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(part => part != "~").ToList();
    }
}
