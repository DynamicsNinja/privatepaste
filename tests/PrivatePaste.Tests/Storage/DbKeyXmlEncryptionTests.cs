using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using PrivatePaste.Server.Config;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Tests.Storage;

public sealed class DbKeyXmlEncryptionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pp-dpkeys-" + Guid.NewGuid().ToString("N"));

    private IDataProtector NewProtector(string dbKey)
    {
        var options = new ServerOptions(dbKey, "pw", _dir, 1024, 1024);
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddDataProtection().SetApplicationName("PrivatePaste").PersistKeysToFileSystem(new DirectoryInfo(_dir));
        services.Configure<KeyManagementOptions>(o => o.XmlEncryptor = new DbKeyXmlEncryptor(options));
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
    }

    [Fact]
    public void Keys_written_by_one_process_are_readable_by_the_next()
    {
        var token = NewProtector("key-0123456789abcdef").Protect("session");
        Assert.Equal("session", NewProtector("key-0123456789abcdef").Unprotect(token));
    }

    [Fact]
    public void Keys_cannot_be_read_with_another_db_key()
    {
        var token = NewProtector("key-0123456789abcdef").Protect("session");
        Assert.ThrowsAny<Exception>(() => NewProtector("other-0123456789abcdef").Unprotect(token));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }
}
