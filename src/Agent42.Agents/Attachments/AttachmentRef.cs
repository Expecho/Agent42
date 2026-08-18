using System.Text.Json.Serialization;

namespace Agent42.Agents.Attachments;

/// <summary>
/// A reference to a stored attachment: enough to fetch its bytes (blob path), address it
/// from the browser (id), and render it (media type, filename) — but never the bytes
/// themselves. Crosses the grain boundary (returned inside <see cref="ChatTurn"/> from
/// <c>GetHistoryAsync</c>, passed into <c>ChatAsync</c>), so it is Orleans-serializable
/// with explicit member ids — mirror <see cref="ChatTurn"/>, not the plain-interface shape.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.AttachmentRef")]
public sealed record AttachmentRef(
    [property: Id(0)] string Id,
    // Internal addressing only — kept out of the HTTP JSON the history endpoint returns (the
    // browser fetches bytes by id through the user-scoped proxy). Orleans serialization keys on
    // [Id], so [JsonIgnore] hides it from System.Text.Json without affecting the grain wire form.
    [property: Id(1)][property: JsonIgnore] string BlobPath,
    [property: Id(2)] string MediaType,
    [property: Id(3)] string FileName);
