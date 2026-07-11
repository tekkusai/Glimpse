using System.IO;

namespace SpacePreview.Utilities;

internal static class AppPaths
{
    private const string DataRootEnvironmentVariable = "GLIMPSE_DATA_ROOT";

    public static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string DataRoot
    {
        get
        {
            return ResolveDataRoot(Environment.GetEnvironmentVariable(DataRootEnvironmentVariable));
        }
    }

    internal static string ResolveDataRoot(string? overrideRoot)
    {
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            try
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(overrideRoot.Trim()));
            }
            catch
            {
                // Fall back to the normal user profile if the override is malformed.
            }
        }

        return Path.Combine(LocalAppData, Branding.DataFolderName);
    }

    public static string InDataRoot(params string[] segments)
    {
        var parts = new string[segments.Length + 1];
        parts[0] = DataRoot;
        Array.Copy(segments, 0, parts, 1, segments.Length);
        return Path.Combine(parts);
    }
}
