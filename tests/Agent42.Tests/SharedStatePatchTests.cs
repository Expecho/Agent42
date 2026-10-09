using System.Text.Json;
using System.Text.Json.Nodes;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The shared-state rules with no grain (ADR 0089 AC11): every JSON Patch op, the caps, and above all that a patch
/// is all-or-nothing, which the library does not guarantee by itself (measured: a failing <c>test</c> after a
/// <c>replace</c> leaves the replace applied).
/// </summary>
public sealed class SharedStatePatchTests
{
    private const string Start = """{"title":"x","checklist":[{"id":"a","done":false}],"n":1}""";

    private static JsonElement Ops(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonNode Applied(string? current, string ops)
    {
        var outcome = SharedStatePatch.Apply(current, Ops(ops));
        Assert.True(outcome.Succeeded, outcome.Error);
        return JsonNode.Parse(outcome.Json!)!;
    }

    [Fact]
    public void EveryOperation_Applies()
    {
        Assert.Equal(1, (int)Applied(Start, """[{"op":"add","path":"/new","value":{"k":1}}]""")["new"]!["k"]!);
        Assert.Equal("b", (string)Applied(Start, """[{"op":"add","path":"/checklist/-","value":{"id":"b"}}]""")["checklist"]![1]!["id"]!);
        Assert.True((bool)Applied(Start, """[{"op":"replace","path":"/checklist/0/done","value":true}]""")["checklist"]![0]!["done"]!);
        Assert.Null(Applied(Start, """[{"op":"remove","path":"/n"}]""")["n"]);

        var moved = Applied(Start, """[{"op":"move","from":"/title","path":"/name"}]""");
        Assert.Equal(("x", (JsonNode?)null), ((string)moved["name"]!, moved["title"]));

        Assert.Equal(1, (int)Applied(Start, """[{"op":"test","path":"/n","value":1}]""")["n"]!);
    }

    // No state yet is an empty object to patch.
    [Fact]
    public void Patch_OnNoState_StartsFromAnEmptyObject()
    {
        Assert.Equal("t", (string)Applied(null, """[{"op":"add","path":"/checklist","value":[{"id":"t"}]}]""")["checklist"]![0]!["id"]!);
    }

    // The core property: a failing op anywhere leaves NOTHING applied, and the stored text is untouched.
    [Fact]
    public void FailedTest_AfterAReplace_ChangesNothing()
    {
        var outcome = SharedStatePatch.Apply(Start, Ops(
            """[{"op":"replace","path":"/n","value":2},{"op":"test","path":"/title","value":"nope"}]"""));

        Assert.False(outcome.Succeeded);
        Assert.Contains("nothing changed", outcome.Error, StringComparison.Ordinal);
        Assert.Null(outcome.Json);
    }

    [Theory]
    [InlineData("""{"op":"add","path":"/a","value":1}""")]
    [InlineData("""[{"op":"remove","path":"/missing"}]""")]
    [InlineData("""[{"op":"frobnicate","path":"/a"}]""")]
    public void InvalidPatches_AreRefused(string ops)
    {
        Assert.False(SharedStatePatch.Apply(Start, Ops(ops)).Succeeded);
    }

    [Fact]
    public void TooManyOperations_AreRefused()
    {
        var ops = "[" + string.Join(",", Enumerable.Range(0, SharedStateLimits.MaxOperations + 1)
            .Select(i => $$"""{"op":"add","path":"/k{{i}}","value":{{i}}}""")) + "]";

        Assert.Contains("At most", SharedStatePatch.Apply(null, Ops(ops)).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Caps_AreEnforcedAfterApplying()
    {
        var big = new string('x', SharedStateLimits.MaxBytes);
        Assert.Contains("KB", SharedStatePatch.Apply(null, Ops($$"""[{"op":"add","path":"/big","value":"{{big}}"}]""")).Error, StringComparison.Ordinal);

        var deep = "1";
        for (var i = 0; i < SharedStateLimits.MaxDepth; i++)
        {
            deep = $$"""{"d":{{deep}}}""";
        }

        Assert.Contains("levels deep", SharedStatePatch.Apply(null, Ops($$"""[{"op":"add","path":"/d","value":{{deep}}}]""")).Error, StringComparison.Ordinal);
    }

    // A user's whole-object edit: an object within the caps is accepted, anything else is refused, and an empty
    // object is stored as "no state".
    [Fact]
    public void Replace_AcceptsAnObject_AndRefusesEverythingElse()
    {
        Assert.Equal("""{"a":1}""", SharedStatePatch.Replace("""{"a":1}""").Json);
        Assert.Null(SharedStatePatch.Replace("{}").Json);
        Assert.True(SharedStatePatch.Replace("{}").Succeeded);
        Assert.False(SharedStatePatch.Replace("[1,2]").Succeeded);
        Assert.False(SharedStatePatch.Replace("\"text\"").Succeeded);
        Assert.False(SharedStatePatch.Replace("{not json").Succeeded);
    }

    // Removing the last key is an empty state, which is stored as null.
    [Fact]
    public void RemovingEverything_StoresNoState()
    {
        var outcome = SharedStatePatch.Apply("""{"a":1}""", Ops("""[{"op":"remove","path":"/a"}]"""));

        Assert.True(outcome.Succeeded);
        Assert.Null(outcome.Json);
    }
}
