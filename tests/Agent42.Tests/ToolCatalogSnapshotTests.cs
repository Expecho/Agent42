using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The name, description and JSON schema of every function tool a default conversation offers the model, against a
/// checked-in golden file. The model reads exactly these three things, and a renamed key lands OFF for anyone who ever
/// pressed Save (CLAUDE.md rule 11), so moving a tool between classes (plan cc-09) must leave them byte-identical.
/// A deliberate tool change regenerates the file: run with <c>AGENT42_UPDATE_TOOL_GOLDEN=1</c>.
/// </summary>
public sealed class ToolCatalogSnapshotTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private static string GoldenPath([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "ToolSchemas.golden.txt");

    [Fact]
    public async Task DefaultToolSet_MatchesSnapshot()
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("tool-catalog-snapshot-conversation");

        var text = new System.Text.StringBuilder();
        await foreach (var evt in agent.ChatAsync(FakeChatClient.ToolSchemaDumpMarker, null))
        {
            if (evt.Type == AgentStreamEventType.TextDelta)
            {
                text.Append(evt.Text);
            }
        }

        var match = Regex.Match(text.ToString(), @"\[toolschemas:(.*?)\[/toolschemas\]", RegexOptions.Singleline);
        Assert.True(match.Success, "the fake did not echo the tool schemas");
        var actual = match.Groups[1].Value.ReplaceLineEndings("\n") + "\n";

        if (Environment.GetEnvironmentVariable("AGENT42_UPDATE_TOOL_GOLDEN") == "1")
        {
            await File.WriteAllTextAsync(GoldenPath(), actual);
        }

        var expected = (await File.ReadAllTextAsync(GoldenPath())).ReplaceLineEndings("\n");
        Assert.Equal(expected, actual);
    }
}
