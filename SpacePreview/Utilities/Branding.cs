namespace SpacePreview.Utilities;

/// <summary>
/// The product's name and voice, in one place. Everything user-facing reads
/// from here, so the identity can never drift out of sync with itself.
/// </summary>
internal static class Branding
{
    public const string Name = "Glimpse";

    public const string Tagline = "One glance at any file.";

    public const string DataFolderName = "Glimpse";

    public const string LogFileName = "Glimpse.log";
}
