using PrivatePaste.Server.Auth;

namespace PrivatePaste.Tests.Auth;

public class PasswordVerifierTests
{
    private readonly PasswordVerifier _verifier = new();

    [Fact]
    public async Task Hash_ThenVerify_Valid()
    {
        var hash = await _verifier.HashAsync("pw-123456");
        Assert.NotNull(hash);
        Assert.StartsWith("argon2id$", hash);
        Assert.Equal(PasswordCheck.Valid, await _verifier.VerifyAsync("pw-123456", hash));
    }

    [Fact]
    public async Task Verify_WrongPassword_Invalid()
    {
        var hash = await _verifier.HashAsync("pw-123456");
        Assert.Equal(PasswordCheck.Invalid, await _verifier.VerifyAsync("pw-1234567", hash));
    }

    [Fact]
    public async Task Verify_NullStored_InvalidAfterHashing() =>
        Assert.Equal(PasswordCheck.Invalid, await _verifier.VerifyAsync("x", null));

    [Fact]
    public async Task Hash_SamePasswordTwice_DifferentSalts()
    {
        var a = await _verifier.HashAsync("same");
        var b = await _verifier.HashAsync("same");
        Assert.NotEqual(a, b);
        Assert.Equal(PasswordCheck.Valid, await _verifier.VerifyAsync("same", a));
        Assert.Equal(PasswordCheck.Valid, await _verifier.VerifyAsync("same", b));
    }

    [Fact]
    public async Task Verify_TooLong_Invalid()
    {
        var tooLong = new string('a', PasswordVerifier.MaxPasswordLength + 1);
        var hash = await _verifier.HashAsync(tooLong[..^1]);
        Assert.Equal(PasswordCheck.Invalid, await _verifier.VerifyAsync(tooLong, hash));
    }

    [Fact]
    public async Task Verify_MalformedStored_Invalid() =>
        Assert.Equal(PasswordCheck.Invalid, await _verifier.VerifyAsync("x", "not-a-hash"));
}
