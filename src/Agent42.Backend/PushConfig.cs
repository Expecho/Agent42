namespace Agent42.Backend;

/// <summary>
/// The server's Web Push configuration (ADR 0028), resolved once at startup from the <c>Push:*</c> section
/// and registered as a singleton. <see cref="Enabled"/> is the single source of the "push is on" fact — it
/// is true exactly when a VAPID private key is configured, the same signal that decides whether Program.cs
/// registers the real <c>WebPushNotificationSender</c> or the <c>NullPushNotificationSender</c>. Centralising
/// it here keeps <c>GET /api/push/config</c> and the sender registration from drifting apart.
///
/// <para><see cref="PublicKey"/> and <see cref="Subject"/> are NOT secret (they are committed in
/// appsettings.json); only the private key is, and it lives in <c>WebPushClientAdapter</c>, never here.</para>
/// </summary>
public sealed record PushConfig(bool Enabled, string PublicKey, string Subject);
