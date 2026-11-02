using PrivatePaste.Desktop.Platform;

namespace PrivatePaste.Tests.Client;

/// <summary>Save from the popup copies received files into Downloads. Tests use their own folder, never the real Downloads.</summary>
public sealed class DownloadsFolderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pp-downloads-" + Guid.NewGuid().ToString("N"));

    public DownloadsFolderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Source(string folder, string name, string content)
    {
        var path = Path.Combine(_dir, folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void The_downloads_folder_is_an_absolute_path()
    {
        Assert.True(Path.IsPathFullyQualified(DownloadsFolder.Path), "not a full path");
        Assert.Equal("Downloads", Path.GetFileName(DownloadsFolder.Path.TrimEnd(Path.DirectorySeparatorChar)), ignoreCase: true);
    }

    [Fact]
    public async Task Clashes_are_numbered_before_the_extension()
    {
        var target = Path.Combine(_dir, "Downloads");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "report.pdf"), "already there");
        File.WriteAllText(Path.Combine(target, "report (2).pdf"), "also there");
        Directory.CreateDirectory(Path.Combine(target, "notes.txt"));   // a folder by that name counts too

        var saved = await DownloadsFolder.SaveAsync(
            [Source("a", "report.pdf", "new report"), Source("b", "notes.txt", "notes"), Source("c", "empty", "")],
            target, default);

        Assert.Equal([Path.Combine(target, "report (3).pdf"), Path.Combine(target, "notes (2).txt"), Path.Combine(target, "empty")], saved);
        Assert.Equal("new report", File.ReadAllText(saved[0]));
        Assert.Equal("notes", File.ReadAllText(saved[1]));
        Assert.Equal("", File.ReadAllText(saved[2]));
        Assert.Equal("already there", File.ReadAllText(Path.Combine(target, "report.pdf")));   // nothing is overwritten
    }

    [Fact]
    public async Task Two_sources_with_the_same_name_both_land()
    {
        var target = Path.Combine(_dir, "Downloads");
        var saved = await DownloadsFolder.SaveAsync([Source("a", "x.txt", "1"), Source("b", "x.txt", "2")], target, default);
        Assert.Equal([Path.Combine(target, "x.txt"), Path.Combine(target, "x (2).txt")], saved);   // the folder is created
        Assert.Equal(["1", "2"], saved.Select(File.ReadAllText));
    }
}
