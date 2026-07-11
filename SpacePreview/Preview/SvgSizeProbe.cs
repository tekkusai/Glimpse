using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Xml;

namespace SpacePreview.Preview;

internal static class SvgSizeProbe
{
    private const int MaxProbeBytes = 128 * 1024;
    private const double MaxCanvasSide = 20000;
    private static readonly Regex NumberRegex = new(@"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    public static bool TryReadCanvasSize(string path, out Size size)
    {
        size = Size.Empty;
        try
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var capped = new MemoryStream();
            var buffer = new byte[Math.Min(MaxProbeBytes, Math.Max(1, (int)Math.Min(file.Length, MaxProbeBytes)))];
            var read = file.Read(buffer, 0, buffer.Length);
            capped.Write(buffer, 0, read);
            capped.Position = 0;

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxProbeBytes
            };
            using var reader = XmlReader.Create(capped, settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (!reader.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return TryResolveCanvasSize(
                    reader.GetAttribute("width"),
                    reader.GetAttribute("height"),
                    reader.GetAttribute("viewBox"),
                    out size);
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    internal static bool TryResolveCanvasSize(string? widthText, string? heightText, string? viewBoxText, out Size size)
    {
        size = Size.Empty;
        var hasWidth = TryParseLength(widthText, out var width);
        var hasHeight = TryParseLength(heightText, out var height);
        var hasViewBox = TryParseViewBox(viewBoxText, out var viewBox);

        if (hasWidth && hasHeight)
        {
            size = new Size(width, height);
            return IsUsable(size);
        }

        if (hasViewBox)
        {
            if (hasWidth)
            {
                size = new Size(width, width / viewBox.Width * viewBox.Height);
                return IsUsable(size);
            }

            if (hasHeight)
            {
                size = new Size(height / viewBox.Height * viewBox.Width, height);
                return IsUsable(size);
            }

            size = new Size(viewBox.Width, viewBox.Height);
            return IsUsable(size);
        }

        return false;
    }

    private static bool TryParseLength(string? text, out double pixels)
    {
        pixels = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value.EndsWith('%'))
        {
            return false;
        }

        var match = NumberRegex.Match(value);
        if (!match.Success || match.Index != 0)
        {
            return false;
        }

        if (!double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        var unit = value[match.Length..].Trim().ToLowerInvariant();
        pixels = unit switch
        {
            "" or "px" => number,
            "pt" => number * 96 / 72,
            "pc" => number * 16,
            "in" => number * 96,
            "cm" => number * 96 / 2.54,
            "mm" => number * 96 / 25.4,
            _ => 0
        };

        return pixels > 0 && double.IsFinite(pixels);
    }

    private static bool TryParseViewBox(string? text, out Size size)
    {
        size = Size.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var matches = NumberRegex.Matches(text);
        if (matches.Count < 4)
        {
            return false;
        }

        if (!double.TryParse(matches[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            || !double.TryParse(matches[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
        {
            return false;
        }

        size = new Size(width, height);
        return IsUsable(size);
    }

    private static bool IsUsable(Size size) =>
        double.IsFinite(size.Width)
        && double.IsFinite(size.Height)
        && size.Width >= 1
        && size.Height >= 1
        && size.Width <= MaxCanvasSide
        && size.Height <= MaxCanvasSide;
}

