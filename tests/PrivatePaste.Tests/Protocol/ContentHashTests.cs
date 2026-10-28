using System.Security.Cryptography;
using System.Text;
using PrivatePaste.Protocol;

namespace PrivatePaste.Tests.Protocol;

public class ContentHashTests
{
    private static string Sha256Hex(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    [Fact] public void Crlf_and_lf_hash_equal() => Assert.Equal(ContentHash.ForText("a\nb"), ContentHash.ForText("a\r\nb"));
    [Fact] public void Lone_cr_normalized() => Assert.Equal("a\nb", ContentHash.NormalizeText("a\rb"));
    [Fact] public void Text_hash_is_lowercase_hex_64() => Assert.Matches("^[0-9a-f]{64}$", ContentHash.ForText("x"));
    [Fact] public void Text_hash_known_vector() => Assert.Equal(Sha256Hex("t:hello"), ContentHash.ForText("hello"));
    [Fact] public void Whitespace_is_significant() => Assert.NotEqual(ContentHash.ForText("a"), ContentHash.ForText("a "));

    [Fact]
    public void Pixel_hash_depends_on_dimensions()
    {
        var px = new byte[8];
        Assert.NotEqual(ContentHash.ForPixels(px, 2, 1), ContentHash.ForPixels(px, 1, 2));
    }

    [Fact]
    public void Pixel_hash_known_vector()
    {
        byte[] px = [1, 2, 3, 4];
        var expected = Convert.ToHexString(SHA256.HashData([.. Encoding.UTF8.GetBytes("i:1x1:"), .. px])).ToLowerInvariant();
        Assert.Equal(expected, ContentHash.ForPixels(px, 1, 1));
    }
}
