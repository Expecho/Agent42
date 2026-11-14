

namespace Agent42.Backend;

// The GET /api/config-health payload (Preferences → Configuration): a diagnostic of which config inputs
// are present/malformed/(opt-in) unreachable. Environment names the host environment (prod-only checks
// render n-a in Development). NEVER carries a secret value — only a status + curated message per check
// (built by ConfigHealth; the never-leak invariant lives there).
public record ConfigHealthReport(string Environment, IReadOnlyList<ConfigCheck> Checks);

// One configuration input's health. Status is ok|warning|error|skipped|na; Message is a CURATED string
// (never an exception or a target URL — the probe path carries the key-bearing geocoding URL). Billable
// flags a check whose live probe costs a metered call/credit, so the UI can label the button.
public record ConfigCheck(string Group, string Key, string Status, string Message, bool Billable);

// The GET /api/access payload: what the UI may show this caller. Advisory, never a security boundary (ADR 0121).
public sealed record AccessResponse(bool IsOwner);
