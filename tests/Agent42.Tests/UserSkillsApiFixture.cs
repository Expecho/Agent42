using Agent42.Agents;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// <see cref="SharedApiFixture"/> for <see cref="UserSkillsApiTests"/>. The two skill services are resolved
/// OPTIONALLY by the handlers (the <c>/api/speech</c> pattern), so registering them is what makes uploads
/// actually work rather than what makes routing work.
///
/// <para>They stay out of the base fixture because only this class needs them, and
/// <see cref="UserSkillProvider"/> materializes real directories under the OS temp folder — giving every
/// host a service that writes to disk buys nothing. It is NOT for cross-class isolation:
/// <c>GET /api/tools</c> reads the uploads from <c>IUserSkillsGrain</c>, never from the package store, so
/// the index it lists is already per-class by virtue of the cluster being per-class.</para>
/// </summary>
public sealed class UserSkillsApiFixture : SharedApiFixture
{
    protected override void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<ISkillPackageStore, InMemorySkillPackageStore>();
        builder.Services.AddSingleton<IUserSkillProvider, UserSkillProvider>();
    }
}
