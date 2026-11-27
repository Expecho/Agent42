using System.Reflection;
using Agent42.Backend;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// <c>AddChatGateway</c> must register the turn pipeline's collaborators (cc-05). The pipeline is registered by type,
/// so a collaborator that is missing fails only when the first chat message resolves it, in production, never in a
/// test that builds the pipeline by hand. A host that registers none of the optional ports (DevHost) must still resolve.
/// </summary>
public sealed class ChatGatewayRegistrationTests
{
    [Fact]
    public void AddChatGateway_ResolvesThePipeline_WithItsCollaborators_AndNoOptionalPorts()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(DispatchProxy.Create<IGrainFactory, UnusedGrainFactory>());
        services.AddChatGateway();
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ChatTurnPipeline>());
        Assert.NotNull(provider.GetRequiredService<ChatModelCommands>());
        Assert.NotNull(provider.GetRequiredService<ChatInboundFiles>());
        Assert.NotNull(provider.GetRequiredService<ChatVoiceTranscription>());
        Assert.Same(provider.GetRequiredService<ChatTurnPipeline>(), provider.GetRequiredService<IChatInboundSink>());
    }

    // Resolution never calls a grain; any call is a bug in the registration.
    public class UnusedGrainFactory : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException("The registration test must not touch a grain.");
    }
}
