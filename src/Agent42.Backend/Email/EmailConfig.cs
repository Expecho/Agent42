namespace Agent42.Backend;

/// <summary>
/// Inbound email configuration (ADR 0095). <see cref="SigningKey"/> is the Mailgun HTTP webhook signing key (a secret,
/// <c>Email__MailgunSigningKey</c>); <see cref="Domain"/> is the non-secret inbound domain whose MX points at Mailgun.
/// </summary>
public sealed record EmailConfig(string SigningKey, string Domain)
{
    /// <summary>
    /// Whether inbound email works end to end. Without a key the endpoint answers 503 and accepts nothing; without a
    /// domain there is no address to show.
    /// </summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(SigningKey) && !string.IsNullOrWhiteSpace(Domain);
}
