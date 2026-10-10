using System.Text.RegularExpressions;

namespace Agent42.Tests;

/// <summary>
/// Guards the Easy Auth session settings in <c>infra/</c> (ADR 0090). TestServer has no Easy Auth, so this
/// asserts the <b>Bicep content</b>, in the source-text style of <see cref="EasyAuthExclusionTests"/>.
///
/// <para>What it stops from regressing: without <c>cookieExpiration</c> the session falls back to the 8-hour
/// platform default, and without <c>login_hint</c> Entra shows its account picker on every sign-in, because
/// both allowed accounts live in one tenant.</para>
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
    public void Module_SendsTheLoginHintOnlyWhenOneIsConfigured()
    {
        var module = Read("infra", "modules", "container-app.bicep");

        Assert.Contains(
            "loginParameters: empty(authLoginHint) ? [] : [ 'login_hint=${authLoginHint}' ]",
            module,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Main_SessionLifetimeIsLongerThanThePlatformDefault()
    {
        var lifetime = ParamDefault(Read("infra", "main.bicep"), "authSessionLifetime");

        // Easy Auth parses it as a .NET TimeSpan; a value that does not parse fails the deploy, not the build.
        Assert.True(TimeSpan.TryParse(lifetime, System.Globalization.CultureInfo.InvariantCulture, out var span),
            $"authSessionLifetime '{lifetime}' is not a TimeSpan");
        Assert.True(span > TimeSpan.FromHours(8), $"authSessionLifetime {span} is not longer than the 8h default");
    }

    [Fact]
    public void Main_LoginHintIsTheCanonicalAllowedAccount()
    {
        var main = Read("infra", "main.bicep");

        // ADR 0074: every byte of data belongs to the canonical account; the alias reaches it via Sign out.
        Assert.Equal("peter.bons@aceve.com", ParamDefault(main, "authLoginHint"));
        Assert.Contains("authLoginHint: authLoginHint", main, StringComparison.Ordinal);
        Assert.Contains("authSessionLifetime: authSessionLifetime", main, StringComparison.Ordinal);
    }

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
