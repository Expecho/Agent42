

namespace Agent42.Backend;

// What the upload endpoint returns for an image: the id to send back on the next turn plus the
// metadata needed to render a preview. The internal blob path is deliberately omitted.
public record AttachmentMetadata(string Id, string MediaType, string FileName);
