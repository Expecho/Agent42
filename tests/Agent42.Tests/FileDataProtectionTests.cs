using Agent42.Backend;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>AC14: the local provider persists the Data Protection key ring on the filesystem (ADR 0018).</summary>
public sealed class FileDataProtectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent42-dp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFileStores(_root, new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }

    [Fact]
    public void TwoProvidersOverTheSameRoot_UnprotectEachOthersPayload()
    {
        string payload;
        using (var first = Build())
        {
            payload = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("secrets").Protect("sk-live-123");
        }

        using var second = Build();
        Assert.Equal("sk-live-123", second.GetRequiredService<IDataProtectionProvider>().CreateProtector("secrets").Unprotect(payload));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_root, "dataprotection"), "key-*.xml"));
    }
}
