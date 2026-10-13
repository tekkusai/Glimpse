using SpacePreview.Native;
using SpacePreview.Preview;

namespace SpacePreview.Services;

/// <summary>Something this PC lacks: what is true without it, and the one fix.</summary>
internal sealed record MissingPiece(string Text, PreviewWindow.PreviewAction Fix);

/// <summary>
/// What this PC lacks that would improve previews. A piece is listed only
/// when its absence is certain; a check that cannot answer lists nothing.
/// AV1 is left out on purpose: WebView2 plays AV1 itself, so a missing
/// Store extension says nothing true about AV1 previews.
/// </summary>
internal static class MissingPieces
{
    /// <summary>Asks Windows. Takes tens of milliseconds: never on the UI thread.</summary>
    public static IReadOnlyList<MissingPiece> Find() => Describe(
        OfficeDocumentPdfConverter.IsAvailable,
        MediaCodecs.HasVideoDecoder(MediaCodecs.HevcVideo),
        MediaCodecs.HasImageDecoder(MediaCodecs.HeifContainer),
        MediaCodecs.HasImageDecoder(MediaCodecs.RawContainer));

    internal static IReadOnlyList<MissingPiece> Describe(bool officeEngine, bool? hevcDecoder, bool? heifDecoder, bool? rawDecoder = null)
    {
        var pieces = new List<MissingPiece>();
        if (!officeEngine)
        {
            pieces.Add(new("Office files open as basic previews.", PreviewWindow.LibreOfficeFix));
        }

        // HEIC is HEIF holding HEVC and needs both decoders, so either line
        // may name it, but only one does.
        if (hevcDecoder == false)
        {
            pieces.Add(new("HEIC photos and HEVC videos won't open.", PreviewWindow.HevcFix));
        }

        if (heifDecoder == false)
        {
            pieces.Add(new(hevcDecoder == false ? "HEIF photos won't open." : "HEIC and HEIF photos won't open.", PreviewWindow.HeifFix));
        }

        if (rawDecoder == false)
        {
            pieces.Add(new("Camera RAW photos won't open.", PreviewWindow.RawFix));
        }

        return pieces;
    }
}
