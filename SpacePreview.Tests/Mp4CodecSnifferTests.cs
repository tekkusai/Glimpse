using System.Buffers.Binary;
using System.Text;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

public sealed class Mp4CodecSnifferTests
{
    [Fact]
    public void ReadsVideoAndAudioCodecsFromSampleDescriptions()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "sample.mp4");
        File.WriteAllBytes(path, Mp4(
            Track("vide", "hvc1"),
            Track("soun", "ac-3")));

        var info = Mp4CodecSniffer.TryRead(path);

        Assert.NotNull(info);
        Assert.Contains("hvc1", info.VideoCodecs);
        Assert.Contains("ac-3", info.AudioCodecs);
        Assert.True(info.HasHevcVideo);
        Assert.False(info.HasAv1Video);
        Assert.Equal("video=hvc1 audio=ac-3", info.DiagnosticText);
    }

    [Fact]
    public void SkipsMediaPayloadBeforeTheMovieHeader()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "sample.mp4");
        File.WriteAllBytes(path, Combine(
            Box("ftyp", Encoding.ASCII.GetBytes("isom\0\0\0\0isom")),
            Box("mdat", new byte[64]),
            Box("moov", Track("vide", "avc1"))));

        var info = Mp4CodecSniffer.TryRead(path);

        Assert.NotNull(info);
        Assert.Contains("avc1", info.VideoCodecs);
        Assert.Empty(info.AudioCodecs);
        Assert.False(info.HasHevcVideo);
        Assert.False(info.HasAv1Video);
    }

    [Fact]
    public void DetectsAv1VideoCodec()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "sample.mp4");
        File.WriteAllBytes(path, Mp4(Track("vide", "av01")));

        var info = Mp4CodecSniffer.TryRead(path);

        Assert.NotNull(info);
        Assert.Contains("av01", info.VideoCodecs);
        Assert.True(info.HasAv1Video);
        Assert.False(info.HasHevcVideo);
    }

    [Fact]
    public void ReadsVideoDisplaySizeFromTrackHeader()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "sample.mp4");
        File.WriteAllBytes(path, Mp4(Track("vide", "avc1", width: 1920, height: 1080)));

        var info = Mp4CodecSniffer.TryRead(path);

        Assert.NotNull(info);
        Assert.Equal(1920, info.DisplayWidth);
        Assert.Equal(1080, info.DisplayHeight);
    }

    [Fact]
    public void AppliesRotatedTrackMatrixToDisplaySize()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "sample.mp4");
        File.WriteAllBytes(path, Mp4(Track("vide", "avc1", width: 1920, height: 1080, rotated: true)));

        var info = Mp4CodecSniffer.TryRead(path);

        Assert.NotNull(info);
        Assert.Equal(1080, info.DisplayWidth);
        Assert.Equal(1920, info.DisplayHeight);
    }

    private static byte[] Mp4(params byte[][] tracks)
    {
        return Combine(
            Box("ftyp", Encoding.ASCII.GetBytes("isom\0\0\0\0isom")),
            Box("moov", Combine(tracks)));
    }

    private static byte[] Track(string handler, string codec, int? width = null, int? height = null, bool rotated = false)
    {
        var tkhd = width is { } w && height is { } h
            ? new[] { Box("tkhd", TrackHeader(w, h, rotated)) }
            : [];
        var mdia = Box("mdia",
            Box("hdlr", Combine(
                new byte[8],
                FourCc(handler),
                new byte[12])),
            Box("minf",
                Box("stbl",
                    Box("stsd", Combine(
                        new byte[4],
                        UInt32(1),
                        Box(codec, new byte[8]))))));

        return Box("trak", tkhd.Concat(new[] { mdia }).ToArray());
    }

    private static byte[] TrackHeader(int width, int height, bool rotated)
    {
        return Combine(
            new byte[4],
            UInt32(0),
            UInt32(0),
            UInt32(1),
            UInt32(0),
            UInt32(0),
            new byte[8],
            UInt16(0),
            UInt16(0),
            UInt16(0),
            UInt16(0),
            Matrix(rotated),
            Fixed16(width),
            Fixed16(height));
    }

    private static byte[] Matrix(bool rotated)
    {
        return rotated
            ? Combine(
                Fixed16(0), Fixed16(1), Int32(0),
                Fixed16(-1), Fixed16(0), Int32(0),
                Int32(0), Int32(0), Int32(0x40000000))
            : Combine(
                Fixed16(1), Fixed16(0), Int32(0),
                Fixed16(0), Fixed16(1), Int32(0),
                Int32(0), Int32(0), Int32(0x40000000));
    }

    private static byte[] Box(string type, params byte[][] payloads)
    {
        var payload = Combine(payloads);
        var result = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), checked((uint)result.Length));
        FourCc(type).CopyTo(result, 4);
        payload.CopyTo(result, 8);
        return result;
    }

    private static byte[] Combine(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    private static byte[] UInt32(uint value)
    {
        var result = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(result, value);
        return result;
    }

    private static byte[] Int32(int value)
    {
        var result = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(result, value);
        return result;
    }

    private static byte[] UInt16(ushort value)
    {
        var result = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(result, value);
        return result;
    }

    private static byte[] Fixed16(int value) => Int32(value << 16);

    private static byte[] FourCc(string value)
    {
        Assert.Equal(4, value.Length);
        return Encoding.ASCII.GetBytes(value);
    }
}
