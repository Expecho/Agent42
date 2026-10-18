

namespace Agent42.Backend;

// The GET /api/push/config payload (ADR 0028): whether push is enabled (a VAPID private key is configured)
// and the non-secret public key the browser passes to pushManager.subscribe. The private key is never here.
public record PushConfigResponse(bool Enabled, string PublicKey);

// Client payload to register a Web Push subscription (ADR 0028): the fields of a browser PushSubscription
// the sender needs to deliver an RFC 8291-encrypted push. Endpoint is the dedup/prune key.
public record PushSubscriptionRequest(string Endpoint, string P256dh, string Auth);

// Client payload for POST /api/push/test: this device's subscription endpoint. Only an endpoint already
// stored for the caller is pushed; keys are never accepted here, so no arbitrary URL is dialled.
public record PushTestRequest(string Endpoint);

// The POST /api/push/test verdict (PushTestOutcomes). StatusCode is the push service's HTTP status on
// "failed" when it returned one. "sent" means the push service accepted it, not that it was displayed.
public record PushTestResponse(string Outcome, int? StatusCode = null);
