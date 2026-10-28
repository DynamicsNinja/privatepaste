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
}
