using System.Text.RegularExpressions;

namespace Agent42.Tests;

/// <summary>
/// Guards that the Azure IaC stays deployable to any tenant (ADR 0128), as source text in the
/// <see cref="EasyAuthSessionTests"/> style: nothing in the suite deploys Bicep or runs the workflow.
///
/// <para>What it stops from regressing: an owner-specific literal creeping back into a template or the workflow, the
/// fail-closed preflight losing a check, the shared infra being deployed after the first <c>az acr login</c>, and the
/// docs drifting from the variables and secrets the workflow reads. ADR 0128 traded "code is the source of truth" for
/// repository variables, and the docs-to-workflow tests in both directions are what compensates.</para>
/// </summary>
public sealed class InfraPortabilityTests
{
    private static readonly string[] OwnerLiterals =
    [
        "crexpechohosting", "cae-hosting", "rg-hosting", "rg-agent42", "agent42.nl",
        "peterbons", "resourceGroups/agent42",
    ];

    [Fact]
    public void NoOwnerLiterals_InBicepCodeOrTheDeployWorkflow()
    {
        var offences = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot(), "infra"), "*.bicep", SearchOption.AllDirectories))
        {
            // @description('...') strings are code, not comments: only // and /* */ are exempt.
            Collect(offences, Path.GetRelativePath(RepoRoot(), file), StripBicepComments(File.ReadAllText(file)));
        }

        Collect(offences, ".github/workflows/deploy.yml", StripYamlComments(Read(".github", "workflows", "deploy.yml")));

        Assert.True(offences.Count == 0, "Owner-specific literals belong in repository variables:\n" + string.Join('\n', offences));

        static void Collect(List<string> offences, string name, string code)
        {
            offences.AddRange(OwnerLiterals
                .Where(l => code.Contains(l, StringComparison.OrdinalIgnoreCase))
                .Select(l => $"{name}: {l}"));
        }
    }

    [Fact]
    public void StripBicepComments_KeepsUrlsInsideStringLiterals()
    {
        var code = StripBicepComments("param a string = 'https://x.example/y' // trailing\n/* block */ param b string = 'it\\'s // not a comment'");

        Assert.Contains("https://x.example/y", code, StringComparison.Ordinal);
        Assert.Contains("it\\'s // not a comment", code, StringComparison.Ordinal);
        Assert.DoesNotContain("trailing", code, StringComparison.Ordinal);
        Assert.DoesNotContain("block", code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("containerRegistryName")]
    [InlineData("containerAppsEnvironmentName")]
    [InlineData("containerAppsEnvironmentResourceGroup")]
    [InlineData("authClientId")]
    public void Main_RequiredParamsHaveNoDefault(string name)
    {
        var main = StripBicepComments(Read("infra", "main.bicep"));

        Assert.Matches(new Regex(@"param " + name + @" string\s*(\r?\n|$)"), main);
    }

    [Fact]
    public void Shared_CreatesTheRegistryAndTheEnvironmentOnlyWhenNoExistingNameIsGiven()
    {
        var shared = StripBicepComments(Read("infra", "shared.bicep"));

        Assert.Contains("var createRegistry = empty(existingRegistryName)", shared, StringComparison.Ordinal);
        Assert.Contains("var createEnvironment = empty(existingEnvironmentName)", shared, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"Microsoft\.ContainerRegistry/registries@[\w-]+' = if \(createRegistry\)"), shared);
        Assert.Matches(new Regex(@"Microsoft\.OperationalInsights/workspaces@[\w-]+' = if \(createEnvironment\)"), shared);
        Assert.Matches(new Regex(@"Microsoft\.App/managedEnvironments@[\w-]+' = if \(createEnvironment\)"), shared);
        // The Container App pulls with admin credentials (container-app.bicep `registries`).
        Assert.Contains("adminUserEnabled: true", shared, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_DeploysTheSharedInfraBeforeTheFirstAcrLogin()
    {
        var workflow = StripYamlComments(Read(".github", "workflows", "deploy.yml"));

        var shared = workflow.IndexOf("infra/shared.bicep", StringComparison.Ordinal);
        var login = workflow.IndexOf("az acr login", StringComparison.Ordinal);
        Assert.True(shared >= 0 && login >= 0, "deploy.yml must deploy shared.bicep and log in to the registry");
        Assert.True(shared < login, "shared.bicep must be deployed before az acr login: the registry may not exist yet");
    }

    [Fact]
    public void Deploy_PreflightRunsFirst_AndGuardsWhatAMissingVariableWouldDestroy()
    {
        var workflow = StripYamlComments(Read(".github", "workflows", "deploy.yml"));

        Assert.Matches(new Regex(@"^  preflight:", RegexOptions.Multiline), workflow);
        // A missing CUSTOM_DOMAIN_NAME expands to "" and would silently unbind the live domain.
        Assert.Contains("customDomains", workflow, StringComparison.Ordinal);
        Assert.Contains("managedEnvironmentId", workflow, StringComparison.Ordinal);
        Assert.Contains("local-dev", workflow, StringComparison.Ordinal);
        Assert.Contains("az cosmosdb list", workflow, StringComparison.Ordinal);

        Assert.Equal("preflight", NeedsOf(workflow, "build-and-push"));
        Assert.Contains("preflight", NeedsOf(workflow, "deploy"), StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_HasNoRoleAssignmentPut()
    {
        // The Foundry grant was dead since ADR 0017 and owner-scoped (ADR 0128).
        var workflow = StripYamlComments(Read(".github", "workflows", "deploy.yml"));

        Assert.DoesNotContain("roleAssignments", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("az rest", workflow, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("vapidPublicKey", "Push__VapidPublicKey")]
    [InlineData("vapidSubject", "Push__VapidSubject")]
    [InlineData("githubOAuthClientId", "GitHub__OAuth__ClientId")]
    [InlineData("emailInboundDomain", "Email__InboundDomain")]
    public void AppConfigOverrides_AreEmittedOnlyWhenSet(string param, string envVar)
    {
        var module = StripBicepComments(Read("infra", "modules", "container-app.bicep"));

        Assert.Matches(new Regex(@"param " + param + @" string = ''"), module);
        Assert.Matches(
            new Regex(@"empty\(" + param + @"\)\s*\?\s*\[\]\s*:\s*\[\{ name: '" + envVar + @"', value: " + param + @" \}\]"),
            module);
        Assert.Single(Regex.Matches(module, "'" + envVar + "'"));
    }

    [Fact]
    public void AppConfigOverrides_AccessOwnersAreEmittedPerIndexOnlyWhenSet()
    {
        var module = StripBicepComments(Read("infra", "modules", "container-app.bicep"));

        Assert.Matches(new Regex(@"param accessOwners array = \[\]"), module);
        Assert.Matches(new Regex(@"empty\(accessOwners\)\s*\?\s*\[\]\s*:\s*map\(range\(0, length\(accessOwners\)\)"), module);
        Assert.Contains("'Access__Owners__${i}'", module, StringComparison.Ordinal);
    }

    [Fact]
    public void AppConfigOverrides_IdentityAliasesAreEmittedPerKeyOnlyWhenSet()
    {
        var module = StripBicepComments(Read("infra", "modules", "container-app.bicep"));

        Assert.Matches(new Regex(@"param identityAliases object = \{\}"), module);
        Assert.Matches(new Regex(@"empty\(identityAliases\)\s*\?\s*\[\]\s*:\s*map\(items\(identityAliases\)"), module);
        Assert.Contains("'Identity__Aliases__${alias.key}'", module, StringComparison.Ordinal);
    }

    [Fact]
    public void Docs_ListEveryVariableAndSecretTheWorkflowReads()
    {
        var docs = Read("docs", "features", "azure-deployment.md");
        var workflow = StripYamlComments(Read(".github", "workflows", "deploy.yml"));

        var missing = Referenced(workflow, "vars").Select(v => "vars." + v)
            .Concat(Referenced(workflow, "secrets").Select(s => "secrets." + s))
            .Where(name => !docs.Contains("`" + name[(name.IndexOf('.') + 1)..] + "`", StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0, "docs/features/azure-deployment.md does not document: " + string.Join(", ", missing));
    }

    [Fact]
    public void Docs_VariablesTableNamesOnlyVariablesTheWorkflowReads()
    {
        var docs = Read("docs", "features", "azure-deployment.md");
        var workflow = StripYamlComments(Read(".github", "workflows", "deploy.yml"));
        var referenced = Referenced(workflow, "vars").ToHashSet();

        var section = SectionOf(docs, "## Variables");
        var named = Regex.Matches(section, @"^\|\s*`(?<name>[A-Z][A-Z0-9_]+)`\s*\|", RegexOptions.Multiline)
            .Select(m => m.Groups["name"].Value)
            .ToList();

        Assert.NotEmpty(named);
        Assert.True(named.All(referenced.Contains),
            "The variables table names variables deploy.yml never reads: " + string.Join(", ", named.Where(n => !referenced.Contains(n))));
    }

    [Fact]
    public void Bootstrap_PrintsOnlySecretsAndVariablesTheWorkflowReads()
    {
        var script = Read("infra", "bootstrap.sh");
        var workflow = StripYamlComments(Read(".github", "workflows", "deploy.yml"));

        var secrets = Regex.Matches(script, @"gh secret set (\w+)").Select(m => m.Groups[1].Value).ToList();
        var variables = Regex.Matches(script, @"gh variable set (\w+)").Select(m => m.Groups[1].Value).ToList();

        Assert.NotEmpty(secrets);
        Assert.NotEmpty(variables);
        Assert.True(secrets.All(Referenced(workflow, "secrets").Contains), "bootstrap.sh prints a secret deploy.yml does not read");
        Assert.True(variables.All(Referenced(workflow, "vars").Contains), "bootstrap.sh prints a variable deploy.yml does not read");
    }

    private static IEnumerable<string> Referenced(string workflow, string context) =>
        Regex.Matches(workflow, @"\b" + context + @"\.([A-Za-z0-9_]+)").Select(m => m.Groups[1].Value).Distinct();

    /// <summary>The <c>needs:</c> value of a job, e.g. <c>preflight</c> or <c>[preflight, build-and-push]</c>.</summary>
    private static string NeedsOf(string workflow, string job)
    {
        var match = Regex.Match(workflow, @"^  " + Regex.Escape(job) + @":\s*\r?\n(?:(?!^  \S).*\r?\n)*?\s+needs:\s*(?<needs>[^\r\n]+)", RegexOptions.Multiline);
        Assert.True(match.Success, $"job '{job}' has no needs:");
        return match.Groups["needs"].Value.Trim();
    }

    private static string SectionOf(string markdown, string heading)
    {
        var start = markdown.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"missing section '{heading}'");
        var next = markdown.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        return next < 0 ? markdown[start..] : markdown[start..next];
    }

    // A single-quoted string (with backslash escapes) matches first, so a // inside it (https://) is never taken for a comment.
    private static readonly Regex BicepTokens = new(
        @"'(?:\\.|[^'\\\r\n])*'|//[^\r\n]*|/\*.*?\*/", RegexOptions.Singleline);

    /// <summary>Removes <c>//</c> and <c>/* */</c> comments but leaves single-quoted strings (and their <c>https://</c>) alone.</summary>
    private static string StripBicepComments(string source) =>
        BicepTokens.Replace(source, m => m.Value[0] == '\'' ? m.Value : string.Empty);

    /// <summary>Drops whole-line <c>#</c> comments. Trailing comments are not used in the workflow.</summary>
    private static string StripYamlComments(string source) =>
        string.Join('\n', source.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

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
