using System.Text.RegularExpressions;

namespace Agent42.Tests;

/// <summary>
/// The deployment path of the Application Insights connection string, as source text in the
/// <see cref="EasyAuthExclusionTests"/> style: nothing in the suite deploys Bicep or runs the workflow. It
/// pins the three hops (GitHub secret → <c>main.bicep</c> param → container <c>secretRef</c>) and the
/// Statsbeat switch, whose absence would be silent egress to Microsoft-owned ingestion keys.
/// </summary>
public sealed class AppInsightsWiringTests
{
    [Fact]
    public void AppInsightsWiring_ContainerAppUsesASecretRefOnlyWhenSupplied()
    {
        var bicep = Read("infra", "modules", "container-app.bicep");

        Assert.Matches(SecureParam("appInsightsConnectionString"), bicep);

        // The env var only ever comes from the secret, never a plain value, and only when one was supplied.
        Assert.Matches(new Regex(
            @"empty\(appInsightsConnectionString\)\s*\?\s*\[\]\s*:\s*\[\{\s*name:\s*'APPLICATIONINSIGHTS_CONNECTION_STRING',\s*secretRef:\s*'appinsights-connection-string'\s*\}\]"),
            bicep);
        Assert.Single(Regex.Matches(bicep, "'APPLICATIONINSIGHTS_CONNECTION_STRING'"));
        Assert.Matches(new Regex(
            @"empty\(appInsightsConnectionString\)\s*\?\s*\[\]\s*:\s*\[\s*\{\s*name:\s*'appinsights-connection-string'\s*value:\s*appInsightsConnectionString\s*\}\s*\]"),
            bicep);
    }

    [Fact]
    public void AppInsightsWiring_StatsbeatIsAlwaysDisabled()
    {
        var bicep = Read("infra", "modules", "container-app.bicep");

        // In the unconditional part of the env array: the switch must hold whether or not a connection
        // string was supplied, so it sits before the first optional env segment (the geocoding key's).
        var statsbeat = bicep.IndexOf("{ name: 'APPLICATIONINSIGHTS_STATSBEAT_DISABLED', value: 'true' }", StringComparison.Ordinal);
        Assert.True(statsbeat >= 0, "APPLICATIONINSIGHTS_STATSBEAT_DISABLED=true is missing from the container env.");
        Assert.True(statsbeat < bicep.IndexOf("'Geocoding__GoogleApiKey'", StringComparison.Ordinal),
            "APPLICATIONINSIGHTS_STATSBEAT_DISABLED must be an unconditional env entry.");
    }

    [Fact]
    public void AppInsightsWiring_MainBicepDeclaresASecureParamAndPassesItThrough()
    {
        var bicep = Read("infra", "main.bicep");

        Assert.Matches(SecureParam("appInsightsConnectionString"), bicep);
        Assert.Contains("appInsightsConnectionString: appInsightsConnectionString", bicep, StringComparison.Ordinal);
    }

    [Fact]
    public void AppInsightsWiring_DeployWorkflowPassesTheSecret()
    {
        var workflow = Read(".github", "workflows", "deploy.yml");

        Assert.Contains(
            "appInsightsConnectionString=\"${{ secrets.APPLICATIONINSIGHTS_CONNECTION_STRING }}\"",
            workflow,
            StringComparison.Ordinal);
    }

    /// <summary><c>@secure()</c>, an optional one-line <c>@description</c>, then the param with an empty default.</summary>
    private static Regex SecureParam(string name) =>
        new(@"@secure\(\)\s*(?:@description\('[^\r\n]*'\)\s*)?param " + name + @" string = ''");

    private static string Read(params string[] relativePath) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. relativePath]));

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
