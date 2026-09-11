using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// A <see cref="SharedApiFixture"/> that also stands up the server-side MCP surface (ADR 0070): the DI
/// half in <see cref="Configure"/> and the middleware plus mapping in <see cref="ConfigureApp"/>.
///
/// <para>This is the whole reason <see cref="SharedApiFixture.ConfigureApp"/> exists. The surface is
/// deliberately not part of <c>MapAgentApi</c> — <c>MapMcp</c> throws without
/// <c>AddMcpServer().WithHttpTransport()</c>, and the other eight fixtures do not register it — so a class
/// that wants it opts in here rather than every host paying for it.</para>
/// </summary>
public sealed class AgentMcpFixture : SharedApiFixture
{
    protected override void Configure(WebApplicationBuilder builder)
        => builder.Services.AddAgentMcpServer();

    protected override void ConfigureApp(WebApplication app)
    {
        // Both halves, in the production order. Mapping without the gate compiles cleanly and serves every
        // exposed agent anonymously, which is precisely what AgentMcp_AnonymousProtocolCall_IsRefused
        // exists to catch — so the fixture must install them exactly as Program.cs does.
        app.UseAgentMcpKeyGate();
        app.MapAgentMcpServer();
    }
}
