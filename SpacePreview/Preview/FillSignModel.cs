using System;
using System.Collections.Generic;

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
/// signature can never rewrite ink the user already put down.
/// </summary>
internal sealed record SignPlacement(
    SignPlacementKind Kind,
    int PageIndex,
    double X,
    double Y,
    double Width,
    double Height,
    string? Text,
    byte[]? ImagePng = null)
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
}

/// <summary>The sign surface's working set for one document.</summary>
internal sealed class SignSession
{
    public List<SignPlacement> Placements { get; } = [];

    public int PageIndex { get; set; }

    public bool HasWork => Placements.Count > 0;
}
