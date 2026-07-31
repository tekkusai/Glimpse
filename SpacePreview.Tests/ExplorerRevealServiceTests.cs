using SpacePreview.Services;

namespace SpacePreview.Tests;

public sealed class ExplorerRevealServiceTests
{
    [Fact]
    public async Task ExistingFileIsSelectedWithAQuotedExplorerArgument()
    {
        using var temp = new TempDirectory();
        var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "folder with spaces")).FullName;
        var path = Path.Combine(folder, "received photo.jpg");
        await File.WriteAllTextAsync(path, "image");

        var startInfo = ExplorerRevealService.CreateStartInfo(path);

        Assert.Equal("explorer.exe", startInfo.FileName);
        Assert.Equal($"/select,\"{path}\"", startInfo.Arguments);
        Assert.True(startInfo.UseShellExecute);
    }

    [Fact]
    public void ExistingFolderIsSelectedInItsParent()
    {
        using var temp = new TempDirectory();
        var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "received")).FullName;

        var startInfo = ExplorerRevealService.CreateStartInfo(folder);

        Assert.Equal($"/select,\"{folder}\"", startInfo.Arguments);
    }
}
