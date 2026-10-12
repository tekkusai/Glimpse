using System.Buffers.Binary;
using System.Text;
using SpacePreview.Preview;
using static SpacePreview.Tests.ImageTestFiles;

namespace SpacePreview.Tests;

/// <summary>
/// A PNG's chunks are read one at a time and not kept, so a file with a
/// great many of them costs what its bytes cost to read and no more, and
/// one walk answers both "does it move" and "can a rotation be saved".
/// </summary>
public sealed class PngChunkWalkTests
{
    // Opening a PNG classifies it for the rotate tooltip. That walk kept a
    // record of every chunk in front of the pixels: 177 MB for a 22 MB file
    // of two million empty chunks.
    [Fact]
    public void ClassifyingAPngKeepsNoRecordOfItsChunks()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "many.png");
        WriteChunkSoup(path, 200_000);

        var persistence = RotationPersistence.ViewOnly;
        var allocated = AllocatedBy(() => persistence = ImageRotationService.Classify(path));

        Assert.Equal(RotationPersistence.Reencode, persistence);
        Assert.True(allocated < 256 * 1024, $"classifying allocated {allocated:N0} bytes");
    }

    [Fact]
    public void ARotationOfAPngWithAGreatManyChunksCarriesThemAllAndKeepsNoRecordOfThem()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "many.png");
        WriteChunkSoup(path, 50_000);
        var before = File.ReadAllBytes(path);

        var result = RotationSaveResult.Failed;
        var allocated = AllocatedBy(() => result = ImageRotationService.SaveQuarterTurns(path, 1));

        Assert.Equal(RotationSaveResult.Saved, result);
        var after = File.ReadAllBytes(path);
        Assert.Equal((3, 5), (BinaryPrimitives.ReadInt32BigEndian(after.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(after.AsSpan(20))));

        // Every one of them is still there, byte for byte, between the header and the pixels.
        Assert.True(before.AsSpan(33, 50_000 * 12).SequenceEqual(after.AsSpan(33, 50_000 * 12)));
        Assert.Equal("IDAT", Encoding.ASCII.GetString(after, 33 + 50_000 * 12 + 4, 4));
        Assert.True(allocated < 2 * 1024 * 1024, $"the save allocated {allocated:N0} bytes");
    }

    // The window walks 64 chunks on its way up and no further. That limit
    // was read as "not an animation", so an animated PNG with a long run
    // of text in front showed its first frame and stood still.
    [Fact]
    public void AnAnimationBehindALongRunOfChunksIsStillFound()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moving.png");
        File.WriteAllBytes(path, ImageRotationCarryTests.AnimatedPng(textChunksInFront: 100));

        var header = ImageHeader.TryRead(path)!;

        Assert.True(header.IsAnimated);
        Assert.True(header.HasAlpha);
        Assert.Equal((5, 3), (header.Width, header.Height));
        Assert.Equal(RotationPersistence.CannotCarry, ImageRotationService.Classify(path));
    }

    // On its way to showing the window the UI thread reads a PNG at a
    // glance, and a glance that ran out of chunks says so. It is not an
    // answer, and it is not kept as one.
    [Fact]
    public void AGlanceThatStopsShortSaysItHasNotDecided()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moving.png");
        var plain = Path.Combine(temp.Path, "plain.png");
        File.WriteAllBytes(path, ImageRotationCarryTests.AnimatedPng(textChunksInFront: 100));
        File.WriteAllBytes(plain, ImageRotationCarryTests.AnimatedPng(textChunksInFront: 10));

        var glance = ImageHeader.TryRead(path, atAGlance: true)!;
        Assert.True(glance.IsUndecided);
        Assert.False(glance.IsAnimated);
        Assert.Equal((5, 3), (glance.Width, glance.Height));

        // The read that is not a glance walks the rest, and from then on
        // the answer is known to a glance too.
        var whole = ImageHeader.TryRead(path)!;
        Assert.True(whole.IsAnimated);
        Assert.False(whole.IsUndecided);
        Assert.Same(whole, ImageHeader.TryRead(path, atAGlance: true));

        // A PNG with a handful of chunks is decided at a glance.
        var ordinary = ImageHeader.TryRead(plain, atAGlance: true)!;
        Assert.True(ordinary.IsAnimated);
        Assert.False(ordinary.IsUndecided);
    }

    // The walk to a PNG's pixels can be long, and the preview it is for may
    // have moved on. It stops when told, and a walk that was called off is
    // not kept as the file's answer.
    [Fact]
    public void AWalkStopsWhenThePreviewHasMovedOn()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "many.png");
        WriteChunkSoup(path, 1000);
        var movedOn = new CancellationToken(canceled: true);

        Assert.Throws<OperationCanceledException>(() => ImageRotationService.Classify(path, movedOn));
        Assert.Throws<OperationCanceledException>(() => ImageHeader.TryRead(path, token: movedOn));

        Assert.Equal((5, 3), (ImageHeader.TryRead(path)!.Width, ImageHeader.TryRead(path)!.Height));
        Assert.Equal(RotationPersistence.Reencode, ImageRotationService.Classify(path));
    }

    /// <summary>Bytes allocated by <paramref name="work"/>, on a thread of its own so nothing else is counted.</summary>
    internal static long AllocatedBy(Action work)
    {
        long allocated = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                work();
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.Start();
        thread.Join();
        Assert.Null(failure);
        return allocated;
    }

    /// <summary>A small valid PNG with <paramref name="count"/> empty private chunks in front of its pixels.</summary>
    internal static void WriteChunkSoup(string path, int count)
    {
        var (chunks, _) = ReadPng(Png(5, 3, 8, 2, new byte[45]));
        var withOne = WritePng([chunks[0], new PngChunk("juNk", []), .. chunks.Skip(1)]);

        // The signature and IHDR are 8 + 25 bytes; the empty chunk is the 12 that follow.
        var empty = withOne.AsSpan(33, 12).ToArray();
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        file.Write(withOne.AsSpan(0, 33));
        for (var i = 0; i < count; i++)
        {
            file.Write(empty);
        }

        file.Write(withOne.AsSpan(45));
    }
}
