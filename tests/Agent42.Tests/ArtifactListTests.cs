using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>The artifact port (plan cc-12; ADR 0084): get, count, add, and a live read of state.</summary>
public sealed class ArtifactListTests
{
    [Fact]
    public void Add_ThenTryGetAndCount()
    {
        var state = new AgentGrainState();
        var list = new ArtifactList(() => state);
        var record = new ArtifactRecord { Id = "art_1", Kind = ArtifactKind.Svg, Title = "Logo" };

        list.Add(record);

        Assert.Equal(1, list.Count);
        Assert.True(list.TryGet("art_1", out var found));
        Assert.Same(record, found);
        Assert.Same(record, state.Artifacts["art_1"]);
    }

    [Theory]
    [InlineData("art_missing")]
    [InlineData("")]
    [InlineData(null)]
    public void TryGet_IsFalseForAnUnknownOrNullId(string? id)
    {
        var list = new ArtifactList(() => new AgentGrainState());

        Assert.False(list.TryGet(id, out _));
    }

    [Fact]
    public void ReadsStateLive_AfterTheStateObjectIsReplaced()
    {
        var state = new AgentGrainState();
        var list = new ArtifactList(() => state);
        list.Add(new ArtifactRecord { Id = "art_1", Kind = ArtifactKind.Svg, Title = "Logo" });

        state = new AgentGrainState();

        Assert.Equal(0, list.Count);
        Assert.False(list.TryGet("art_1", out _));
    }
}
