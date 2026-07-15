using Azure.Core;

namespace Agent42.Tests;

/// <summary>
/// A <see cref="TokenCredential"/> that hands back a fixed token without contacting anything. Lets the
/// model-provider tests construct a real <c>AzureOpenAIClient</c>/<c>ArmClient</c> — neither asks for a
/// token at construction time, which is exactly why the eager startup check in Program.cs is free.
/// </summary>
internal sealed class StaticTokenCredential : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => new("token", DateTimeOffset.UtcNow.AddHours(1));

    public override ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext, CancellationToken cancellationToken)
        => new(GetToken(requestContext, cancellationToken));
}
