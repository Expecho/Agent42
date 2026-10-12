namespace Agent42.Backend;

// GET /api/email/config (ADR 0095): whether inbound email is on, and the domain the editor appends to an agent's
// EmailToken to show its address. Never the signing key.
public record EmailConfigResponse(bool Enabled, string Domain);
