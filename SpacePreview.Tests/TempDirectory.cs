namespace SpacePreview.Tests;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        // Under the run's folder, so a file still held at Dispose goes with the run.
        Path = System.IO.Path.Combine(TestDataRoot.RunRoot, "files", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string CreateFile(string name, string? contents = null)
    {
        var path = System.IO.Path.Combine(Path, name);
        File.WriteAllText(path, contents ?? name);
        return path;
    }

    public string CreateDirectory(string name)
    {
        var path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        // Best effort: temp files should never hide the real test failure.
        if (Directory.Exists(Path))
        {
            TestDataRoot.TryDelete(Path);
        }
    }
}
