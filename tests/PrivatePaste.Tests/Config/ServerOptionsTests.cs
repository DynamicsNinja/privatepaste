using Microsoft.Extensions.Configuration;
using PrivatePaste.Server.Config;

namespace PrivatePaste.Tests.Config;

public class ServerOptionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static readonly string GoodKey = new('k', 16);

    [Fact]
    public void Missing_db_key_throws() => Assert.Contains("PP_DB_KEY",
        Assert.Throws<InvalidOperationException>(() => ServerOptions.FromConfiguration(Config(("PP_PASSWORD", "x")))).Message);

    [Fact]
    public void Missing_password_throws() => Assert.Contains("PP_PASSWORD",
        Assert.Throws<InvalidOperationException>(() => ServerOptions.FromConfiguration(Config(("PP_DB_KEY", GoodKey)))).Message);

    [Fact]
    public void Short_db_key_throws()
    {
        var msg = Assert.Throws<InvalidOperationException>(() =>
            ServerOptions.FromConfiguration(Config(("PP_DB_KEY", "short"), ("PP_PASSWORD", "x")))).Message;
        Assert.Contains("PP_DB_KEY", msg);
        Assert.Contains("16", msg);
    }

    [Fact]
    public void Defaults_apply()
    {
        var o = ServerOptions.FromConfiguration(Config(("PP_DB_KEY", GoodKey), ("PP_PASSWORD", "p")));
        Assert.Equal("/data", o.DataDir);
        Assert.Equal(20L * 1024 * 1024, o.MaxImageBytes);
        Assert.Equal(1024L * 1024, o.MaxTextBytes);
    }

    [Fact]
    public void Overrides_apply()
    {
        var o = ServerOptions.FromConfiguration(Config(
            ("PP_DB_KEY", GoodKey), ("PP_PASSWORD", "p"), ("PP_MAX_IMAGE_MB", "5"), ("PP_MAX_TEXT_KB", "10")));
        Assert.Equal(5L * 1024 * 1024, o.MaxImageBytes);
        Assert.Equal(10L * 1024, o.MaxTextBytes);
    }

    [Theory]
    [InlineData("PP_MAX_IMAGE_MB", "abc")]
    [InlineData("PP_MAX_IMAGE_MB", "0")]
    [InlineData("PP_MAX_TEXT_KB", "-1")]
    public void Invalid_size_throws(string key, string value)
    {
        var msg = Assert.Throws<InvalidOperationException>(() => ServerOptions.FromConfiguration(
            Config(("PP_DB_KEY", GoodKey), ("PP_PASSWORD", "p"), (key, value)))).Message;
        Assert.Contains(key, msg);
    }
}
