using System.Text.Json;
using PrivatePaste.Protocol;

namespace PrivatePaste.Tests.Protocol;

public class ContentClassifierTests
{
    [Theory]
    [InlineData("https://github.com/dokploy/dokploy", ClipKind.Link)]
    [InlineData("  http://x.io/a?b=1  ", ClipKind.Link)]
    [InlineData("see https://x.io", ClipKind.Text)]
    [InlineData("ftp://x.io", ClipKind.Text)]
    [InlineData("https://a.io\nhttps://b.io", ClipKind.Text)]
    [InlineData("github.com", ClipKind.Text)]
    public void Classifies(string text, ClipKind expected) => Assert.Equal(expected, ContentClassifier.ClassifyText(text));

    public static TheoryData<string, bool> SharedCases()
    {
        var path = Path.Combine(Web.PlatformIconsTests.RepoRoot(), "docs", "classifier-test-vectors.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var data = new TheoryData<string, bool>();
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
            data.Add(c.GetProperty("text").GetString()!, c.GetProperty("link").GetBoolean());
        return data;
    }

    // Android's ClipCodec.isLink checks the same file.
    [Theory]
    [MemberData(nameof(SharedCases))]
    public void Matches_the_shared_vectors(string text, bool link) =>
        Assert.Equal(link ? ClipKind.Link : ClipKind.Text, ContentClassifier.ClassifyText(text));
}
