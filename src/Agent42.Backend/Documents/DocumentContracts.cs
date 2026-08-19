

namespace Agent42.Backend;

// What the upload endpoint returns for a document: a kind marker (so the client renders a chip and
// never sends it back as an attachmentId — documents are retrieval-only, ADR 0007), the filename,
// and how many chunks were indexed (0 = no extractable text, e.g. a scanned PDF).
public record DocumentUploadResult(string Kind, string FileName, string MediaType, int ChunkCount);
