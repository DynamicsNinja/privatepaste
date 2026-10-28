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

    // Hard-coded cross-client vectors: the Android and desktop ports must produce exactly these (also in README).
    [Theory]
    [InlineData("hello", "1bec6ebade142b8ea787b2744bbb12d1915e0d0d5e8e31f7f5452911840de3a9")]
    [InlineData("a\nb", "b77cc1fcd69063256ffc07884bde76c875b73e56a786925feedd7ba881f0616c")]
    [InlineData("a\r\nb", "b77cc1fcd69063256ffc07884bde76c875b73e56a786925feedd7ba881f0616c")]
    [InlineData("šećer \U0001F680", "812328f074c3d5a025a329dd4674a676d83d5f926bd79af643a51e783ed3ac39")] // "šećer 🚀"
    public void Text_hash_cross_client_vectors(string text, string expected) => Assert.Equal(expected, ContentHash.ForText(text));

    [Fact]
    public void Pixel_hash_cross_client_vector_1x1_opaque_red() =>
        Assert.Equal("8d833d22068fdf91eecbd0d310208548d1f9418c6513c572a7c7d01fc696c49a",
            ContentHash.ForPixels([255, 0, 0, 255], 1, 1));
}
