namespace Agent42.Backend;

/// <summary>
/// Registers inbound email (ADR 0096) — the feature's own startup wiring, kept out of <c>Program.cs</c> for the S3776
/// reason <see cref="HindsightRegistration"/> records. The endpoint resolves both services optionally, so a host that
/// skips this still routes and answers 503.
/// </summary>
public static class EmailRegistration
{
    /// <summary>Reads <c>Email:MailgunSigningKey</c> (secret) and <c>Email:InboundDomain</c>, the replay guard and the plain-name rate limiter (<c>Email:PlainNameMaxPerHour</c>, 0 = unlimited).</summary>
    public static void AddInboundEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new EmailConfig(
            configuration["Email:MailgunSigningKey"] ?? "",
            configuration["Email:InboundDomain"] ?? ""));
        services.AddSingleton<MailgunReplayGuard>();
        services.AddSingleton(new PlainEmailRateLimiter(
            configuration.GetValue("Email:PlainNameMaxPerHour", PlainEmailRateLimiter.DefaultMaxPerHour)));
    }
}
