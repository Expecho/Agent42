namespace Agent42.Backend;

/// <summary>
/// The Google connection as the Preferences → Calendar tab sees it (ADR 0053).
///
/// <para><b>It carries no token, and it never will.</b> The only fields are whether the deployment can
/// offer a connection at all, whether this user has made one, and which account it is —
/// <c>Status_NeverEchoesTokens</c> pins that by asserting on the serialized response rather than by
/// trusting this comment.</para>
/// </summary>
/// <param name="Configured">
/// Whether <c>GoogleCalendar:ClientId</c>/<c>ClientSecret</c> are present. False means the deployment
/// cannot offer a connection, so the UI explains that instead of offering a Connect button that
/// would 409.
/// </param>
/// <param name="Connected">Whether this user has a stored connection.</param>
/// <param name="AccountEmail">The connected account, read from the primary calendar's id; null when not connected.</param>
/// <param name="ConnectedAt">When the connection was made; null when not connected.</param>
public sealed record GoogleStatusResponse(
    bool Configured, bool Connected, string? AccountEmail, DateTimeOffset? ConnectedAt);
