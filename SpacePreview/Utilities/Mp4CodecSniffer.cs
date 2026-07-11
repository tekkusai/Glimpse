using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace SpacePreview.Utilities;

internal sealed record Mp4CodecInfo(
    IReadOnlyList<string> VideoCodecs,
    IReadOnlyList<string> AudioCodecs,
    int? DisplayWidth = null,
    int? DisplayHeight = null)
{
    public bool HasHevcVideo => VideoCodecs.Any(codec =>
        codec.Equals("hvc1", StringComparison.OrdinalIgnoreCase)
        || codec.Equals("hev1", StringComparison.OrdinalIgnoreCase)
        || codec.Equals("dvh1", StringComparison.OrdinalIgnoreCase)
        || codec.Equals("dvhe", StringComparison.OrdinalIgnoreCase));

    public bool HasAv1Video => VideoCodecs.Any(codec =>
        codec.Equals("av01", StringComparison.OrdinalIgnoreCase));

    public string DiagnosticText
    {
        get
        {
            var parts = new List<string>(2);
            if (VideoCodecs.Count > 0)
            {
                parts.Add("video=" + string.Join(",", VideoCodecs));
            }

            if (AudioCodecs.Count > 0)
            {
                parts.Add("audio=" + string.Join(",", AudioCodecs));
            }

            return parts.Count == 0 ? "none" : string.Join(" ", parts);
        }
    }
}

internal static class Mp4CodecSniffer
{
    private const int MaxBoxes = 4096;
    private const int MaxDepth = 12;
    private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");

    public static Mp4CodecInfo? TryRead(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".mov", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var state = new ParseState();
            ParseContainer(stream, 0, stream.Length, null, state, 0);
            return state.Video.Count == 0 && state.Audio.Count == 0 && state.DisplayWidth is null
                ? null
                : new Mp4CodecInfo(state.Video.ToArray(), state.Audio.ToArray(), state.DisplayWidth, state.DisplayHeight);
        }
        catch
        {
            return null;
        }
    }

    private static void ParseContainer(Stream stream, long start, long end, string? handler, ParseState state, int depth)
    {
        if (depth > MaxDepth || start < 0 || end > stream.Length || start >= end)
        {
            return;
        }

        var localHandler = handler;
        var position = start;
        while (position + 8 <= end && state.BoxesRead < MaxBoxes)
        {
            if (!TryReadBoxHeader(stream, position, end, out var box))
            {
                return;
            }

            state.BoxesRead++;
            if (box.Type.Equals("hdlr", StringComparison.Ordinal))
            {
                localHandler = ReadHandlerType(stream, box);
            }
            else if (box.Type.Equals("stsd", StringComparison.Ordinal))
            {
                ReadSampleDescriptions(stream, box, localHandler, state);
            }
            else if (box.Type.Equals("trak", StringComparison.Ordinal))
            {
                ReadTrack(stream, box, state, depth + 1);
            }
            else if (IsContainer(box.Type))
            {
                ParseContainer(stream, box.PayloadStart, box.End, localHandler, state, depth + 1);
            }

            position = box.End;
        }
    }

    private static bool IsContainer(string type)
    {
        return type is "moov" or "trak" or "mdia" or "minf" or "stbl";
    }

    private static void ReadTrack(Stream stream, Box box, ParseState state, int depth)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        TrackDimensions? dimensions = null;
        string? handler = null;
        var position = box.PayloadStart;
        while (position + 8 <= box.End && state.BoxesRead < MaxBoxes)
        {
            if (!TryReadBoxHeader(stream, position, box.End, out var child))
            {
                return;
            }

            state.BoxesRead++;
            if (child.Type.Equals("tkhd", StringComparison.Ordinal))
            {
                dimensions = ReadTrackDimensions(stream, child);
            }
            else if (child.Type.Equals("mdia", StringComparison.Ordinal))
            {
                handler ??= FindHandlerType(stream, child, state, depth + 1);
                ParseContainer(stream, child.PayloadStart, child.End, null, state, depth + 1);
            }

            position = child.End;
        }

        if (handler?.Equals("vide", StringComparison.Ordinal) == true
            && dimensions is { } video
            && state.DisplayWidth is null)
        {
            state.DisplayWidth = video.Width;
            state.DisplayHeight = video.Height;
        }
    }

    private static string? FindHandlerType(Stream stream, Box box, ParseState state, int depth)
    {
        if (depth > MaxDepth)
        {
            return null;
        }

        var position = box.PayloadStart;
        while (position + 8 <= box.End && state.BoxesRead < MaxBoxes)
        {
            if (!TryReadBoxHeader(stream, position, box.End, out var child))
            {
                return null;
            }

            state.BoxesRead++;
            if (child.Type.Equals("hdlr", StringComparison.Ordinal))
            {
                return ReadHandlerType(stream, child);
            }

            if (IsContainer(child.Type)
                && FindHandlerType(stream, child, state, depth + 1) is { } handler)
            {
                return handler;
            }

            position = child.End;
        }

        return null;
    }

    private static TrackDimensions? ReadTrackDimensions(Stream stream, Box box)
    {
        Span<byte> version = stackalloc byte[1];
        if (box.PayloadStart + 1 > box.End)
        {
            return null;
        }

        stream.Position = box.PayloadStart;
        if (stream.Read(version) != 1)
        {
            return null;
        }

        if (version[0] is not 0 and not 1)
        {
            return null;
        }

        var matrixOffset = box.PayloadStart + (version[0] == 1 ? 52 : 40);
        var widthOffset = matrixOffset + 36;
        if (widthOffset + 8 > box.End)
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[44];
        stream.Position = matrixOffset;
        if (stream.Read(buffer) != buffer.Length)
        {
            return null;
        }

        var a = ReadFixed16(buffer[..4]);
        var b = ReadFixed16(buffer.Slice(4, 4));
        var c = ReadFixed16(buffer.Slice(12, 4));
        var d = ReadFixed16(buffer.Slice(16, 4));
        var width = ReadUnsignedFixed16(buffer.Slice(36, 4));
        var height = ReadUnsignedFixed16(buffer.Slice(40, 4));
        if (width is < 8 or > 16384 || height is < 8 or > 16384)
        {
            return null;
        }

        var quarterTurn = Math.Abs(a) < 0.01
            && Math.Abs(d) < 0.01
            && Math.Abs(Math.Abs(b) - 1) < 0.01
            && Math.Abs(Math.Abs(c) - 1) < 0.01;
        if (quarterTurn)
        {
            (width, height) = (height, width);
        }

        return new TrackDimensions((int)Math.Round(width), (int)Math.Round(height));
    }

    private static string? ReadHandlerType(Stream stream, Box box)
    {
        Span<byte> buffer = stackalloc byte[4];
        var offset = box.PayloadStart + 8;
        if (offset + buffer.Length > box.End)
        {
            return null;
        }

        stream.Position = offset;
        return stream.Read(buffer) == buffer.Length ? FourCc(buffer) : null;
    }

    private static void ReadSampleDescriptions(Stream stream, Box box, string? handler, ParseState state)
    {
        Span<byte> countBuffer = stackalloc byte[4];
        var countOffset = box.PayloadStart + 4;
        var position = box.PayloadStart + 8;
        if (position > box.End || countOffset + countBuffer.Length > box.End)
        {
            return;
        }

        stream.Position = countOffset;
        if (stream.Read(countBuffer) != countBuffer.Length)
        {
            return;
        }

        var entries = BinaryPrimitives.ReadUInt32BigEndian(countBuffer);
        for (uint i = 0; i < entries && position + 8 <= box.End; i++)
        {
            if (!TryReadBoxHeader(stream, position, box.End, out var entry))
            {
                return;
            }

            if (handler?.Equals("vide", StringComparison.Ordinal) == true)
            {
                state.Video.Add(entry.Type);
            }
            else if (handler?.Equals("soun", StringComparison.Ordinal) == true)
            {
                state.Audio.Add(entry.Type);
            }

            position = entry.End;
        }
    }

    private static bool TryReadBoxHeader(Stream stream, long position, long limit, out Box box)
    {
        box = default;
        Span<byte> header = stackalloc byte[16];
        if (position + 8 > limit)
        {
            return false;
        }

        stream.Position = position;
        if (stream.Read(header[..8]) != 8)
        {
            return false;
        }

        var smallSize = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        var type = FourCc(header.Slice(4, 4));
        long headerSize = 8;
        long size = smallSize;
        if (smallSize == 1)
        {
            if (position + 16 > limit || stream.Read(header.Slice(8, 8)) != 8)
            {
                return false;
            }

            size = unchecked((long)BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8)));
            headerSize = 16;
        }
        else if (smallSize == 0)
        {
            size = limit - position;
        }

        if (size < headerSize || position + size > limit)
        {
            return false;
        }

        box = new Box(type, position + headerSize, position + size);
        return true;
    }

    private static string FourCc(ReadOnlySpan<byte> bytes) => Latin1.GetString(bytes).ToLowerInvariant();

    private readonly record struct Box(string Type, long PayloadStart, long End);

    private readonly record struct TrackDimensions(int Width, int Height);

    private sealed class ParseState
    {
        public HashSet<string> Video { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Audio { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int BoxesRead { get; set; }
        public int? DisplayWidth { get; set; }
        public int? DisplayHeight { get; set; }
    }

    private static double ReadFixed16(ReadOnlySpan<byte> value) =>
        BinaryPrimitives.ReadInt32BigEndian(value) / 65536.0;

    private static double ReadUnsignedFixed16(ReadOnlySpan<byte> value) =>
        BinaryPrimitives.ReadUInt32BigEndian(value) / 65536.0;
}
