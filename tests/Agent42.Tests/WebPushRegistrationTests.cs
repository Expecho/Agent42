using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace Agent42.Tests;

/// <summary>
/// The one startup decision Web Push owns (ADR 0028): push is on only when BOTH the VAPID private and public key
/// are present. Tested directly because <c>Program.cs</c> is run by no test host.
/// </summary>
public sealed class WebPushRegistrationTests
{
    private static ServiceProvider Build(params (string Key, string? Value)[] entries)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(System.Reflection.DispatchProxy.Create<IGrainFactory, UnusedProxy>());
        services.AddWebPush(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void NoKeys_PushIsOff_WithTheNoOpSender()
    {
        using var sp = Build();

        Assert.False(sp.GetRequiredService<PushConfig>().Enabled);
        Assert.IsType<NullPushNotificationSender>(sp.GetRequiredService<IPushNotificationSender>());
        Assert.Null(sp.GetService<IWebPushClient>());
    }

    // Half-configured (secret set, public key not yet pasted) must read as off: the browser cannot subscribe
    // without the public key.
    [Theory]
    [InlineData("secret", "")]
    [InlineData("", "public")]
    [InlineData("  ", "public")]
    public void OnlyOneKey_PushStaysOff(string privateKey, string publicKey)
    {
        using var sp = Build(("Push:VapidPrivateKey", privateKey), ("Push:VapidPublicKey", publicKey));

        Assert.False(sp.GetRequiredService<PushConfig>().Enabled);
        Assert.IsType<NullPushNotificationSender>(sp.GetRequiredService<IPushNotificationSender>());
    }

    [Fact]
    public void BothKeys_RegisterTheRealSender_AndExposeThePublicKeyAndSubject()
    {
        var keys = WebPush.VapidHelper.GenerateVapidKeys();
        using var sp = Build(
            ("Push:VapidPrivateKey", keys.PrivateKey),
            ("Push:VapidPublicKey", keys.PublicKey),
            ("Push:VapidSubject", "mailto:test@agent42"));

        var config = sp.GetRequiredService<PushConfig>();
        Assert.True(config.Enabled);
        Assert.Equal(keys.PublicKey, config.PublicKey);
        Assert.Equal("mailto:test@agent42", config.Subject);
        Assert.IsType<WebPushNotificationSender>(sp.GetRequiredService<IPushNotificationSender>());
        Assert.IsType<WebPushClientAdapter>(sp.GetRequiredService<IWebPushClient>());
        Assert.NotNull(sp.GetRequiredService<PushTestNotifier>());
    }

    [Fact]
    public void TheWebPushClient_HasTheSendBudgetTimeout()
    {
        var keys = WebPush.VapidHelper.GenerateVapidKeys();
        using var sp = Build(("Push:VapidPrivateKey", keys.PrivateKey), ("Push:VapidPublicKey", keys.PublicKey));

        using var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(WebPushClientAdapter.HttpClientName);

        Assert.Equal(WebPushNotificationSender.SendBudget, http.Timeout);
    }

    // Resolved by the senders' constructors but never called here.
    public class UnusedProxy : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
            => throw new NotSupportedException();
    }
}
