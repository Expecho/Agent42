using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// <see cref="SharedApiFixture"/> for <see cref="EmailApiTests"/>: inbound email ENABLED with a known signing key, so a
/// test can sign a Mailgun-shaped POST itself (ADR 0096). The base fixture registers no <see cref="EmailConfig"/>, which
/// is the no-key case <see cref="EmailApiNoKeyTests"/> asserts on.
/// </summary>
public sealed class EmailApiFixture : SharedApiFixture
{
    public const string SigningKey = "test-mailgun-signing-key";
    public const string Domain = "in.test.example";

    protected override void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(new EmailConfig(SigningKey, Domain));
        builder.Services.AddSingleton<MailgunReplayGuard>();
    }
}
