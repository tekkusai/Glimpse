using System.Diagnostics;
using System.Runtime.InteropServices;
using SpacePreview.Native;

namespace SpacePreview.Services;

internal enum PreviewAccessWarningKind
{
    ConfirmedElevationMismatch,
    AccessUnavailable
}

internal sealed record PreviewAccessWarningInfo(
    string Key,
    string DisplayName,
    uint ProcessId,
    PreviewAccessWarningKind Kind);

internal static class ProcessIntegrityService
{
    private static readonly Lazy<bool?> CurrentProcessElevation = new(QueryCurrentProcessElevation);

    public static bool TryGetPreviewAccessWarning(
        IntPtr window,
        out PreviewAccessWarningInfo? warning)
    {
        warning = null;
        if (window == IntPtr.Zero)
        {
            return false;
        }

        var processId = NativeMethods.GetWindowProcessId(window);
        var currentElevated = CurrentProcessElevation.Value;
        bool? targetElevated = null;
        if (processId != 0 && TryIsProcessElevated(processId, out var elevated))
        {
            targetElevated = elevated;
        }

        var kind = ClassifyWarning(currentElevated, targetElevated);
        if (kind is null)
        {
            return false;
        }

        var processName = TryGetProcessName(processId);
        var displayName = string.IsNullOrWhiteSpace(processName) ? "This app" : processName;
        var key = string.IsNullOrWhiteSpace(processName)
            ? processId == 0 ? $"hwnd:{window.ToInt64():X}" : $"pid:{processId}"
            : processName.ToUpperInvariant();
        warning = new PreviewAccessWarningInfo(key, displayName, processId, kind.Value);
        return true;
    }

    internal static PreviewAccessWarningKind? ClassifyWarning(
        bool? currentElevated,
        bool? targetElevated)
    {
        if (currentElevated is null || targetElevated is null)
        {
            return PreviewAccessWarningKind.AccessUnavailable;
        }

        return IsMismatch(currentElevated, targetElevated)
            ? PreviewAccessWarningKind.ConfirmedElevationMismatch
            : null;
    }

    internal static bool IsMismatch(bool? currentElevated, bool? targetElevated) =>
        currentElevated == false && targetElevated == true;

    internal static bool TryIsProcessElevated(uint processId, out bool elevated)
    {
        elevated = false;
        var process = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            inheritHandle: false,
            processId);
        if (process == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return TryReadElevation(process, out elevated);
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    private static bool? QueryCurrentProcessElevation() =>
        TryReadElevation(NativeMethods.GetCurrentProcess(), out var elevated)
            ? elevated
            : null;

    private static bool TryReadElevation(IntPtr process, out bool elevated)
    {
        elevated = false;
        if (!NativeMethods.OpenProcessToken(
                process,
                NativeMethods.TOKEN_QUERY,
                out var token)
            || token == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var size = Marshal.SizeOf<NativeMethods.TOKEN_ELEVATION>();
            if (!NativeMethods.GetTokenInformation(
                    token,
                    NativeMethods.TOKEN_INFORMATION_CLASS_ELEVATION,
                    out var information,
                    size,
                    out _))
            {
                return false;
            }

            elevated = information.TokenIsElevated != 0;
            return true;
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }

    private static string? TryGetProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }
}
