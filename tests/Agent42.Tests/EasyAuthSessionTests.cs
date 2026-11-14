using System.Text.RegularExpressions;

namespace Agent42.Tests;

/// <summary>
/// Guards the Easy Auth session and access settings in <c>infra/</c> (ADR 0090, amended by ADR 0121). TestServer has
/// no Easy Auth, so this asserts the <b>Bicep content</b>, in the source-text style of
/// <see cref="EasyAuthExclusionTests"/>.
///
/// <para>What it stops from regressing: without <c>cookieExpiration</c> the session falls back to the 8-hour
/// platform default; and the invite-only access model (ADR 0121) is "assignment required" on the enterprise app
/// alone, so neither an <c>allowedPrincipals</c> copy nor a <c>login_hint</c> may creep back in (a hint would pin
/// every invited guest to one account at Entra).</para>
/// </summary>
public sealed class EasyAuthSessionTests
{
    [Fact]
    public void Module_SetsAFixedSessionLifetimeFromItsParameter()
    {
        var module = Read("infra", "modules", "container-app.bicep");

        Assert.Contains("convention: 'FixedTime'", module, StringComparison.Ordinal);
        Assert.Contains("timeToExpiration: authSessionLifetime", module, StringComparison.Ordinal);
    }

    [Fact]
    public void Main_SessionLifetimeIsLongerThanThePlatformDefault()
    {
        var main = Read("infra", "main.bicep");
        var lifetime = ParamDefault(main, "authSessionLifetime");

        // Easy Auth parses it as a .NET TimeSpan; a value that does not parse fails the deploy, not the build.
        Assert.True(TimeSpan.TryParse(lifetime, System.Globalization.CultureInfo.InvariantCulture, out var span),
            $"authSessionLifetime '{lifetime}' is not a TimeSpan");
        Assert.True(span > TimeSpan.FromHours(8), $"authSessionLifetime {span} is not longer than the 8h default");
        Assert.Contains("authSessionLifetime: authSessionLifetime", main, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthConfig_HasNoAllowedPrincipalsCopy_AssignmentOnTheEnterpriseAppIsTheOnlyAllowlist()
    {
        var module = Read("infra", "modules", "container-app.bicep");
        var main = Read("infra", "main.bicep");

        // The code, not the comments: the comment block names these words on purpose.
        var code = StripBicepComments(module);
        Assert.DoesNotContain("allowedPrincipals", code, StringComparison.Ordinal);
        Assert.DoesNotContain("authAllowedUserObjectIds", StripBicepComments(main), StringComparison.Ordinal);
        Assert.DoesNotContain("authAllowedUserObjectIds", code, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthConfig_SendsNoLoginHint()
    {
        var code = StripBicepComments(Read("infra", "modules", "container-app.bicep"))
            + StripBicepComments(Read("infra", "main.bicep"));

        Assert.DoesNotContain("login_hint", code, StringComparison.Ordinal);
        Assert.DoesNotContain("authLoginHint", code, StringComparison.Ordinal);
        Assert.DoesNotContain("loginParameters", code, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthConfig_StillRedirectsUnauthenticatedBrowsersToTheTenantIssuer()
    {
        var module = Read("infra", "modules", "container-app.bicep");

        Assert.Contains("unauthenticatedClientAction: 'RedirectToLoginPage'", module, StringComparison.Ordinal);
        // Single-tenant issuer: invited guests are directory members, so the issuer must not be widened (ADR 0121).
        Assert.Contains("openIdIssuer: '${environment().authentication.loginEndpoint}${authTenantId}/v2.0'", module,
            StringComparison.Ordinal);
    }

    private static string StripBicepComments(string bicep) =>
        Regex.Replace(bicep, @"//[^\r\n]*", string.Empty);

    private static string ParamDefault(string bicep, string name)
    {
        var match = Regex.Match(bicep, $@"param {name} string = '([^']*)'");
        Assert.True(match.Success, $"No string default for param {name} in main.bicep");
        return match.Groups[1].Value;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "Could not locate repo root (Agent42.slnx) from " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
