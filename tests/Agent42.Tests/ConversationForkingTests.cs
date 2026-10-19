using System.Text.Json;
using Agent42.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// AC9 (ADR 0106): the edit/fork helpers of ADR 0092 without a silo. <see cref="EditRegenerateForkTests"/> and
/// <c>ConversationEditForkApiTests</c> still pin the grain's refusals, persist pipeline and API end to end.
/// </summary>
public sealed class ConversationForkingTests
{
    private const string CreateArtifact = "create_artifact";
    private const string UpdateArtifact = "update_artifact";

    private readonly InMemoryAttachmentStore _store = new();
    private readonly List<ChatMessage> _messages = [];
    private readonly List<List<AttachmentRef>> _attachments = [];
    private readonly Dictionary<string, AttachmentRef> _images = [];
    private readonly Dictionary<string, ArtifactRecord> _artifacts = [];

    private ConversationForking Forking() => new(_store, NullLogger.Instance);

    private EditContext Context() => new(_messages, _attachments, _images, _artifacts);

    private void Turn(string text)
    {
        _messages.Add(new ChatMessage(ChatRole.User, text));
        _messages.Add(new ChatMessage(ChatRole.Assistant, text + " reply"));
        _attachments.Add([]);
    }

    // An artifact tool call and its JSON result, as the session stores them.
    private static IEnumerable<ChatMessage> ArtifactCall(string tool, string artifactId, int version)
    {
        var callId = Guid.NewGuid().ToString("N");
        yield return new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, tool)]);
        yield return new ChatMessage(ChatRole.Tool,
        [
            new FunctionResultContent(callId, JsonSerializer.Serialize(
                new ArtifactInfo(artifactId, version, ArtifactKind.Markdown, "Doc"), JsonSerializerOptions.Web)),
        ]);
    }

    private static ArtifactRecord Artifact(string id, params int[] versions) => new()
    {
        Id = id,
        Kind = ArtifactKind.Markdown,
        Title = "Doc",
        Versions = [.. versions.Select(v => new ArtifactVersionRef(v, $"{id}-v{v}", 10, DateTimeOffset.UnixEpoch))],
    };

    [Fact]
    public void Token_MatchesOnlyTheCountAndTheLastTextTogether()
    {
        List<string> users = ["first", "second"];
        var hash = ConversationForking.HistoryTextHash("second");

        Assert.True(ConversationForking.TokenMatches(users, 2, hash));
        Assert.True(ConversationForking.TokenMatches(users, 2, hash.ToUpperInvariant()));
        Assert.False(ConversationForking.TokenMatches(users, 1, hash));
        Assert.False(ConversationForking.TokenMatches(users, 2, ConversationForking.HistoryTextHash("first")));
        Assert.True(ConversationForking.TokenMatches([], 0, ConversationForking.HistoryTextHash("")));
    }

    // The browser computes the same hex SHA-256 with crypto.subtle.digest.
    [Fact]
    public void HistoryTextHash_IsLowerHexSha256OfUtf8()
        => Assert.Equal(
            "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
            ConversationForking.HistoryTextHash("hello"));

    // Only the removed turn's artifact versions go: an older version of the same artifact stays.
    [Fact]
    public void RemoveLastTurn_DropsOnlyTheVersionsTheRemovedTurnCreated()
    {
        Turn("make it");
        _messages.InsertRange(1, ArtifactCall(CreateArtifact, "art_1", 1));
        _messages.Add(new ChatMessage(ChatRole.User, "change it"));
        _messages.AddRange(ArtifactCall(UpdateArtifact, "art_1", 2));
        _attachments.Add([]);
        _artifacts["art_1"] = Artifact("art_1", 1, 2);

        Forking().RemoveLastTurn(Context(), HistoryRetention.UserTurnIndices(_messages));

        Assert.Equal([1], _artifacts["art_1"].Versions.Select(v => v.Version));
        Assert.Equal("make it reply", _messages[^1].Text);
    }

    // The creating turn's removal takes the artifact record with its last version.
    [Fact]
    public void RemoveLastTurn_RemovesTheArtifactTheTurnCreated()
    {
        Turn("before");
        _messages.Add(new ChatMessage(ChatRole.User, "make it"));
        _messages.AddRange(ArtifactCall(CreateArtifact, "art_1", 1));
        _attachments.Add([]);
        _artifacts["art_1"] = Artifact("art_1", 1);

        Forking().RemoveLastTurn(Context(), HistoryRetention.UserTurnIndices(_messages));

        Assert.Empty(_artifacts);
    }

    [Fact]
    public void RemoveLastTurn_DropsTheTurnsGeneratedImage_AndTruncatesTheAttachments()
    {
        Turn("before");
        _messages.Add(new ChatMessage(ChatRole.User, "draw"));
        _messages.Add(new ChatMessage(ChatRole.Assistant, [new UriContent("https://img/1", "image/png")])
        {
            AdditionalProperties = new() { ["assistantImageId"] = "gen-1" },
        });
        _attachments.Add([]);
        _attachments.Add([]); // an orphan trailing entry from a failed run
        _images["gen-1"] = new AttachmentRef("gen-1", "k/gen-1", "image/png", "gen.png");

        Forking().RemoveLastTurn(Context(), HistoryRetention.UserTurnIndices(_messages));

        Assert.Empty(_images);
        Assert.Single(_attachments);
    }

    // A version a result names that the conversation no longer holds is ignored, not an error.
    [Fact]
    public void ArtifactsWithout_IgnoresDanglingVersions_AndCopiesDeeply()
    {
        _artifacts["art_1"] = Artifact("art_1", 1, 2);

        var copies = Forking().ArtifactsWithout(_artifacts, [("art_1", 2), ("art_missing", 1)]);

        var copy = Assert.Single(copies);
        Assert.Equal([1], copy.Versions.Select(v => v.Version));
        Assert.Equal([1, 2], _artifacts["art_1"].Versions.Select(v => v.Version));
    }

    [Fact]
    public void KeptGeneratedImages_KeepsOnlyReferencedOnes()
    {
        var kept = new AttachmentRef("gen-1", "k/gen-1", "image/png", "a.png");
        _images["gen-1"] = kept;
        _images["gen-2"] = new AttachmentRef("gen-2", "k/gen-2", "image/png", "b.png");
        List<ChatMessage> messages =
        [
            new(ChatRole.Assistant, "x") { AdditionalProperties = new() { ["assistantImageId"] = "gen-1" } },
        ];

        var images = Forking().KeptGeneratedImages(messages, _images);

        Assert.Same(kept, Assert.Single(images).Value);
    }

    // Each blob is copied once under the fork's key, however often the payload names it.
    [Fact]
    public async Task CopyForkBlob_CopiesUnderTheForkKey_OncePerId()
    {
        var source = await _store.SaveAsync("forking-src/conv", "a.png", "image/png", [1, 2, 3]);
        var copied = new Dictionary<string, AttachmentRef>(StringComparer.Ordinal);

        var first = await Forking().CopyForkBlobAsync(source, "forking-dst/conv", copied);
        var second = await Forking().CopyForkBlobAsync(source, "forking-dst/conv", copied);

        Assert.Equal($"forking-dst/conv/{source.Id}", first.BlobPath);
        Assert.Same(first, second);
        Assert.NotNull(await _store.GetAsync("forking-dst/conv", source.Id));
    }

    // A blob that cannot be copied keeps its id under the new key rather than losing the fork.
    [Fact]
    public async Task CopyForkBlob_MissingSource_KeepsTheIdUnderTheNewKey()
    {
        var missing = new AttachmentRef("missing", "nowhere/missing", "image/png", "m.png");

        var copy = await Forking().CopyForkBlobAsync(missing, "forking-dst2/conv", []);

        Assert.Equal("forking-dst2/conv/missing", copy.BlobPath);
    }

    // A version whose blob is gone is dropped, and an artifact with none left is dropped with it.
    [Fact]
    public async Task CopyForkArtifacts_DropsVersionsWhoseBlobIsMissing()
    {
        var stored = await _store.SaveAsync("forking-art-src/conv", "v1.md", "text/markdown", [1]);
        var kept = new ArtifactRecord
        {
            Id = "art_1",
            Kind = ArtifactKind.Markdown,
            Title = "Doc",
            Versions =
            [
                new ArtifactVersionRef(1, stored.Id, 1, DateTimeOffset.UnixEpoch),
                new ArtifactVersionRef(2, "gone", 1, DateTimeOffset.UnixEpoch),
            ],
        };
        var fork = new ConversationFork(
            "forking-art-src/conv", null, null, null, null, null, null, "{}", [], [], [kept, Artifact("art_2", 1)]);

        var artifacts = await Forking().CopyForkArtifactsAsync(fork, "forking-art-dst/conv");

        Assert.Equal(["art_1"], artifacts.Keys);
        Assert.Equal([1], artifacts["art_1"].Versions.Select(v => v.Version));
        Assert.NotNull(await _store.GetAsync("forking-art-dst/conv", stored.Id));
    }
}
