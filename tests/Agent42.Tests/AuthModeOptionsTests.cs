using Agent42.Backend;
using Microsoft.Extensions.Configuration;

namespace Agent42.Tests;

public sealed class AuthModeOptionsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("easyauth")]
    [InlineData("EasyAuth")]
    public void AuthMode_Absent_IsEasyAuth(string? value)
        => Assert.Equal(AuthMode.EasyAuth, AuthModeSetting.Read(Config(("Auth:Mode", value))));

    [Theory]
    [InlineData("entra")]
    [InlineData(" ENTRA ")]
    public void AuthMode_Entra_IsRecognised(string value)
        => Assert.Equal(AuthMode.Entra, AuthModeSetting.Read(Config(("Auth:Mode", value))));

    [Fact]
    public void AuthMode_Garbage_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => AuthModeSetting.Read(Config(("Auth:Mode", "oauth"))));
        Assert.Contains("Use 'easyauth' or 'entra'", ex.Message);
    }

    [Fact]
    public void Entra_MissingSettings_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => EntraAuthSettings.Read(Config(("Auth:Entra:ClientId", "c"))));
        Assert.Contains("Auth:Entra:TenantId", ex.Message);
        Assert.Contains("Auth:Entra:ClientSecret", ex.Message);
        Assert.DoesNotContain("Auth:Entra:ClientId", ex.Message);
    }

    [Fact]
    public void Entra_AllSettings_Reads()
    {
        var s = EntraAuthSettings.Read(Config(
            ("Auth:Entra:TenantId", "t"), ("Auth:Entra:ClientId", "c"), ("Auth:Entra:ClientSecret", "s"),
            ("Auth:PublicBaseUrl", "https://chat.example.com/")));
        Assert.Equal(("t", "c", "s", "https://chat.example.com"), (s.TenantId, s.ClientId, s.ClientSecret, s.PublicBaseUrl));
    }

    [Fact]
    public void Entra_RelativePublicBaseUrl_Throws()
        => Assert.Throws<InvalidOperationException>(() => EntraAuthSettings.Read(Config(
            ("Auth:Entra:TenantId", "t"), ("Auth:Entra:ClientId", "c"), ("Auth:Entra:ClientSecret", "s"),
            ("Auth:PublicBaseUrl", "/chat"))));
}
