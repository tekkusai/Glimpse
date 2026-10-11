using System;

namespace SpacePreview.Preview;

internal enum SignPlacementKind
{
    Signature,
    Date,
    Stamp
}

/// <summary>
/// One placed element in sign mode. Coordinates are normalized 0..1 in
/// DISPLAYED page space with a top-left origin: what the sign surface shows,
/// deliberately ignorant of PDF user space. The writer owns the translation
/// to page boxes, bottom-left origin, and rotation. Image placements carry
/// their pixels from the moment of placement, so replacing the stored
/// signature can never rewrite ink the user already put down. Source names
/// the stored file those pixels came from; only drafts read it.
/// </summary>
internal sealed record SignPlacement(
    SignPlacementKind Kind,
    int PageIndex,
    double X,
    double Y,
    double Width,
    double Height,
    string? Text,
    byte[]? ImagePng = null,
    SignInkSource? Source = null)
{
    /// <summary>Kept inside the page with a working minimum size.</summary>
    public SignPlacement Clamped()
    {
        var width = Math.Clamp(Width, 0.02, 1);
        var height = Math.Clamp(Height, 0.01, 1);
        return this with
        {
            Width = width,
            Height = height,
            X = Math.Clamp(X, 0, 1 - width),
            Y = Math.Clamp(Y, 0, 1 - height)
        };
    }

    /// <summary>
    /// A text frame as wide as its text at its height, around the same
    /// center. <paramref name="pageAspect"/> is the displayed page's height
    /// over its width. Text too long for the page shrinks with its frame.
    /// </summary>
    public SignPlacement FittedToText(double pageAspect)
    {
        if (Text is null || !double.IsFinite(pageAspect) || pageAspect <= 0)
        {
            return Clamped();
        }

        var height = Height;
        var width = SignText.FrameAspect(Text) * height * pageAspect;
        if (width > 0.9)
        {
            height *= 0.9 / width;
            width = 0.9;
        }

        return (this with
        {
            X = X + Width / 2 - width / 2,
            Y = Y + Height / 2 - height / 2,
            Width = width,
            Height = height
        }).Clamped();
    }
}

/// <summary>
/// The saved signature or stamp a placement's pixels came from, and a
/// fingerprint of that file's bytes when they were read: a stamp replaced
/// since is a different image under the same name. Redrawn ink was edited
/// on the pad after placing, so no stored file holds it any more.
/// </summary>
internal sealed record SignInkSource(string File, string Fingerprint, bool Redrawn = false);

/// <summary>
/// Placed text is Helvetica in the file and Arial on screen. The two share
/// every advance width, so one table sizes the frame for both, and the
/// frame, the screen and the saved file can never disagree about where the
/// last digit ends. Only WinAnsi characters exist here: anything else would
/// look right on screen and print as a question mark.
/// </summary>
internal static class SignText
{
    /// <summary>Font size as a share of the frame height.</summary>
    public const double FontScale = 0.72;

    /// <summary>Baseline above the frame's bottom edge, as a share of its height.</summary>
    public const double BaselineScale = 0.26;

    /// <summary>Room on each side of the text, in ems.</summary>
    private const double SidePadEm = 0.1;

    /// <summary>Frame width over frame height for this text.</summary>
    public static double FrameAspect(string text) => (WidthEm(text) + 2 * SidePadEm) * FontScale;

    /// <summary>The text's advance width in ems.</summary>
    public static double WidthEm(string text)
    {
        var units = 0;
        foreach (var ch in text)
        {
            units += TryEncode(ch, out var code) ? Widths[code - 32] : Widths[0];
        }

        return units / 1000.0;
    }

    /// <summary>The text with every character the file cannot carry removed.</summary>
    public static string Writable(string text)
    {
        var kept = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (TryEncode(ch, out _))
            {
                kept.Append(ch);
            }
        }

        return kept.ToString();
    }

    /// <summary>The character's WinAnsi code, the encoding the writer's font uses.</summary>
    public static bool TryEncode(char ch, out byte code)
    {
        code = 0;
        if (ch is >= ' ' and <= '~' or >= '\u00A0' and <= '\u00FF')
        {
            code = (byte)ch;
            return true;
        }

        var index = WinAnsiExtras.IndexOf(ch);
        if (index < 0)
        {
            return false;
        }

        code = (byte)(0x80 + index);
        return true;
    }

    // WinAnsi 0x80..0x9F; a space marks the five codes it leaves undefined.
    private const string WinAnsiExtras =
        "\u20AC \u201A\u0192\u201E\u2026\u2020\u2021\u02C6\u2030\u0160\u2039\u0152 \u017D " +
        " \u2018\u2019\u201C\u201D\u2022\u2013\u2014\u02DC\u2122\u0161\u203A\u0153 \u017E\u0178";

    // Helvetica advance widths for WinAnsi 0x20..0xFF, in thousandths of an em.
    private static readonly short[] Widths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, // 0x20
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556, // 0x30
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, // 0x40
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556, // 0x50
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, // 0x60
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584, 278, // 0x70
        556, 278, 222, 556, 333, 1000, 556, 556, 333, 1000, 667, 333, 1000, 278, 611, 278, // 0x80
        278, 222, 222, 333, 333, 350, 556, 1000, 333, 1000, 500, 333, 944, 278, 500, 667, // 0x90
        278, 333, 556, 556, 556, 556, 260, 556, 333, 737, 370, 556, 584, 333, 737, 333, // 0xA0
        400, 584, 333, 333, 333, 556, 537, 278, 333, 333, 365, 556, 834, 834, 834, 611, // 0xB0
        667, 667, 667, 667, 667, 667, 1000, 722, 667, 667, 667, 667, 278, 278, 278, 278, // 0xC0
        722, 722, 778, 778, 778, 778, 778, 584, 778, 722, 722, 722, 722, 667, 667, 611, // 0xD0
        556, 556, 556, 556, 556, 556, 889, 500, 556, 556, 556, 556, 278, 278, 278, 278, // 0xE0
        556, 556, 556, 556, 556, 556, 556, 584, 611, 556, 556, 556, 556, 500, 556, 500 // 0xF0
    ];
}
