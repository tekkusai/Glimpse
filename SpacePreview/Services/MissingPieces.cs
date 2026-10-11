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
        MediaCodecs.HasImageDecoder(MediaCodecs.HeifContainer));

    internal static IReadOnlyList<MissingPiece> Describe(bool officeEngine, bool? hevcDecoder, bool? heifDecoder)
    {
        var pieces = new List<MissingPiece>();
        if (!officeEngine)
        {
            pieces.Add(new("Word, Excel and PowerPoint open as basic previews.", PreviewWindow.LibreOfficeFix));
        }

        // HEIC is HEIF holding HEVC and needs both decoders, so either line
        // may name it.
        if (hevcDecoder == false)
        {
            pieces.Add(new("HEIC photos and HEVC videos can't be previewed.", PreviewWindow.HevcFix));
        }

        if (heifDecoder == false)
        {
            pieces.Add(new("HEIC and HEIF photos can't be previewed.", PreviewWindow.HeifFix));
        }

        return pieces;
    }
}
