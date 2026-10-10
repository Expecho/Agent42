namespace Agent42.Backend;

// PUT /api/agents/{id}/webhook (ADR 0094): the owner's https URL, and whether to mint a new signing secret. The
// first save always mints one.
public record WebhookUpsertRequest(string Url, bool RotateSecret = false);

// The PUT response. Secret is the plaintext signing secret, present ONCE — only in the response that minted it — and
// never stored or returned again (the ADR 0022 key shape).
public record WebhookSaveResponse(string Url, bool HasSecret, string? Secret);

// POST /api/agents/{id}/webhook/test: what the content-free test delivery got back. An error class, never a body.
public record WebhookTestResponse(int? StatusCode, string? Error, int Attempts, long DurationMs);
