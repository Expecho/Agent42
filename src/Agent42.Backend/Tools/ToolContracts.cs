

namespace Agent42.Backend;

// Client payload for PUT /api/conversations/{id}/tools: the per-conversation tool override (ADR 0021), a
// list of ToolCatalog keys. The UI always sends an explicit list (never null); unknown keys are dropped
// server-side by the grain.
public record ToolSelectionRequest(IReadOnlyList<string>? Tools);
