using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// The engine is a launcher and the process it starts. Both are gone when
/// a run ends, before the folder they wrote into is deleted, and neither
/// can be left behind by a launcher that started its child early. cmd and
/// the ping it starts stand in for soffice.exe and soffice.bin; the file
/// ping writes to stands in for the lu*.tmp files soffice.bin holds open.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class OfficeEngineRunTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public void WhatARunStartedIsGoneBeforeItsFolderIsDeleted()
    {
        using var temp = new TempDirectory();
        var staging = temp.CreateDirectory("staging");
        var held = Path.Combine(staging, "lu1.tmp");
        using var launcher = StartLauncher(held);
        var control = new OfficeRunControl();
        control.Attach(launcher);
        WaitForChild(held);

        control.End();

        Assert.True(launcher.HasExited);
        // No waiting and no second try: a process still alive holds the file.
        Directory.Delete(staging, recursive: true);
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void AChildTheLauncherStartedBeforeTheJobTookItEndsWithTheRunToo()
    {
        using var temp = new TempDirectory();
        var staging = temp.CreateDirectory("staging");
        var held = Path.Combine(staging, "lu1.tmp");
        using var launcher = StartLauncher(held);
        WaitForChild(held);
        var control = new OfficeRunControl();
        control.Attach(launcher);

        control.End();

        Assert.True(launcher.HasExited);
        Directory.Delete(staging, recursive: true);
        Assert.False(Directory.Exists(staging));
    }

    // Converting a real document, when this machine has the engine: the run
    // is stopped once soffice.bin is up, and nothing of it is left.
    [Fact]
    public async Task AStoppedConversionLeavesNothingBehind()
    {
        if (!OfficeDocumentPdfConverter.IsAvailable)
        {
            return;
        }

        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "long.docx");
        WriteLongDocument(path);
        using var preview = new CancellationTokenSource();

        var job = OfficeDocumentPdfConverter.Convert(path, OfficeKind.Document, preview.Token);
        await WaitForEngineScratchAsync(job.Key);
        preview.Cancel();
        Assert.Null(await job.Complete.WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Empty(Directory.EnumerateDirectories(job.Key, ".staging-*"));

        // The profile is free at once: a run that met a dying soffice.bin
        // there would hand it the request and fail.
        var next = Path.Combine(temp.Path, "next.docx");
        OfficeDocumentPdfConverterTests.WriteLinkedImageDocument(next, "https://example.com/", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink");
        var after = OfficeDocumentPdfConverter.Convert(next, OfficeKind.Document, CancellationToken.None);
        Assert.NotNull(await after.Complete.WaitAsync(TimeSpan.FromSeconds(90)));
    }

    /// <summary>Hundreds of pages of text: the engine is still at work when the test stops it.</summary>
    private static void WriteLongDocument(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Entry(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
            </Types>
            """);
        Entry(archive, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
            </Relationships>
            """);
        var body = new StringBuilder();
        for (var line = 0; line < 30_000; line++)
        {
            body.Append("<w:p><w:r><w:t>Line ").Append(line).Append(" of a long report that takes the engine a while.</w:t></w:r></w:p>");
        }

        Entry(archive, "word/document.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>{body}</w:body></w:document>
            """);
    }

    private static void Entry(ZipArchive archive, string name, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(text);
    }

    /// <summary>cmd, which starts a ping that writes to <paramref name="held"/> for two minutes.</summary>
    private static Process StartLauncher(string held) =>
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 120 127.0.0.1 > \"{held}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

    /// <summary>ping has started once its first reply is in the file.</summary>
    private static void WaitForChild(string held)
    {
        var clock = Stopwatch.StartNew();
        while (!File.Exists(held) || new FileInfo(held).Length == 0)
        {
            Assert.True(clock.Elapsed < Patience, "The launcher never started its child.");
            Thread.Sleep(20);
        }
    }

    /// <summary>soffice.bin is up once it has written into the run's scratch folder.</summary>
    private static async Task WaitForEngineScratchAsync(string cacheDirectory)
    {
        var clock = Stopwatch.StartNew();
        while (!Directory.Exists(cacheDirectory)
               || !Directory.EnumerateFiles(cacheDirectory, "*", SearchOption.AllDirectories).Any(file => file.Contains("engine-tmp", StringComparison.Ordinal)))
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "The engine never started.");
            await Task.Delay(20);
        }
    }
}
