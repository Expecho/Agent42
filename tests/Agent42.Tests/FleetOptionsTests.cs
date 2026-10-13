using Agent42.Backend;
using Microsoft.Extensions.Configuration;

namespace Agent42.Tests;

/// <summary>The agent fleet's options (ADR 0097): they bind from <c>Fleet</c>, and the shipped values are sane.</summary>
public class FleetOptionsTests
{
    [Fact]
    public void Binds_FromFleetSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Fleet:MaxTeamDepth"] = "3",
                ["Fleet:MaxTokensPerTaskLimit"] = "500",
                ["Fleet:CopilotAgentsEnabled"] = "false",
                ["Fleet:DefaultCopilotModel"] = "foundry:gpt-5",
            })
            .Build();

        var options = FleetOptions.From(configuration);

        Assert.Equal(3, options.MaxTeamDepth);
        Assert.Equal(500, options.MaxTokensPerTaskLimit);
        Assert.False(options.CopilotAgentsEnabled);
        Assert.Equal("foundry:gpt-5", options.DefaultCopilotModel);
        // Untouched values keep the engine's defaults.
        Assert.Equal(7, options.PassingScore);
    }

    [Fact]
    public void MissingSection_GivesDefaults()
    {
        var options = FleetOptions.From(new ConfigurationBuilder().Build());

        Assert.Equal(6, options.MaxTeamDepth);
        Assert.Equal(40, options.MaxAgentsPerTask);
        Assert.Equal(0.8, options.BudgetSoftLimit);
        Assert.True(options.DefaultMaxTokensPerTask > 0);
    }

    /// <summary>The page proposes the default, and the server refuses anything over the limit, so a default over the
    /// limit would make every task submitted with the default fail.</summary>
    [Fact]
    public void ShippedConfiguration_DefaultTokensWithinLimit()
    {
        var options = FleetOptions.From(new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepoRoot(), "src", "Agent42.Backend", "appsettings.json"))
            .Build());

        Assert.InRange(options.DefaultMaxTokensPerTask, 1, options.MaxTokensPerTaskLimit);
        Assert.True(options.MaxConcurrentTasks >= 1);
        Assert.InRange(options.BudgetSoftLimit, 0.1, 1.0);
        Assert.True(options.MaxEventLogBytes > 0 && options.MaxWorkspaceBytes > 0 && options.MaxWorkspaceFiles > 0);
    }

    internal static string RepoRoot()
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
