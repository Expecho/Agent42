using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// The <c>chat-turns</c> container is a fixed literal in code and in Bicep (the <c>agent-fleet</c> precedent, ADR 0153). Nothing
/// in this suite can reach Azure, so a rename on one side only would fail first in production; this pins both. It also pins
/// that the container carries no lifecycle rule: retention is the app's own sweeper, which covers every provider (D10).
/// </summary>
public sealed class ChatTurnContainerNameTests
{
    [Fact]
    public void ContainerName_IsTheSameLiteralInCodeAndBicep()
    {
        var bicep = File.ReadAllText(Path.Combine(RepoRoot(), "infra", "modules", "storage.bicep")).ReplaceLineEndings("\n");

        Assert.Equal("chat-turns", AzureChatTurnBlobs.ContainerName);
        Assert.Contains($"name: '{AzureChatTurnBlobs.ContainerName}'", bicep);
        Assert.Contains("chatTurnsContainer", bicep);
    }

    [Fact]
    public void ChatTurnsContainer_HasNoPublicAccess_AndNoLifecyclePolicy()
    {
        var bicep = File.ReadAllText(Path.Combine(RepoRoot(), "infra", "modules", "storage.bicep")).ReplaceLineEndings("\n");
        var block = bicep[bicep.IndexOf("resource chatTurnsContainer", StringComparison.Ordinal)..];
        block = block[..block.IndexOf("\n}", StringComparison.Ordinal)];

        Assert.Contains("publicAccess: 'None'", block);
        Assert.DoesNotContain("managementPolicies", bicep);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "Could not locate repo root (Agent42.slnx) from " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
