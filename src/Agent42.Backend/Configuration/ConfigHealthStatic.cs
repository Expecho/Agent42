namespace Agent42.Backend;

public static partial class ConfigHealth
{
    /// <summary>Days after its release at which the bundled signal-cli is flagged, then refused as stale.</summary>
    public const int SignalCliWarnAfterDays = 75;

    /// <summary>Signal's clients expire after three months, and "signal-cli releases older than three months may not work".</summary>
    public const int SignalCliErrorAfterDays = 90;

    /// <summary>
    /// The age of the signal-cli the image bundles (ADR 0111, phase 3), from the <c>SIGNAL_CLI_VERSION</c> and
    /// <c>SIGNAL_CLI_RELEASED</c> the Dockerfile bakes in. signal-cli's README: "The official Signal clients expire after
    /// three months and then the Signal-Server can make incompatible changes. So signal-cli releases older than three
    /// months may not work correctly." Nothing upgrades it automatically, so this row is how the operator learns it is
    /// time to bump the Dockerfile. Not applicable while Signal is off.
    /// </summary>
    private static ConfigCheck SignalCliCheck(IConfiguration config, DateTimeOffset now)
    {
        const string key = "SIGNAL_CLI_RELEASED";
        if (!config.GetValue<bool>("ChatGateway:Signal:Enabled"))
        {
            return new ConfigCheck(GroupOptional, key, NotApplicable, "Signal is off (ChatGateway:Signal:Enabled).", Billable: false);
        }

        var version = config["SIGNAL_CLI_VERSION"];
        if (!DateOnly.TryParseExact(config[key], "yyyy-MM-dd", out var released) || string.IsNullOrEmpty(version))
        {
            return new ConfigCheck(GroupOptional, key, Warning,
                "Signal is on, but the image doesn't say which signal-cli it bundles, so its age is unknown.", Billable: false);
        }

        var age = DateOnly.FromDateTime(now.UtcDateTime).DayNumber - released.DayNumber;
        var (status, verdict) = age >= SignalCliErrorAfterDays
            ? (Error, "past Signal's three-month client expiry: update signal-cli in the backend Dockerfile now")
            : age >= SignalCliWarnAfterDays
                ? (Warning, "approaching Signal's three-month client expiry: update signal-cli in the backend Dockerfile")
                : (Ok, "current");
        return new ConfigCheck(GroupOptional, key, status,
            $"signal-cli {version}, released {released:yyyy-MM-dd} ({age} days ago): {verdict}.", Billable: false);
    }

    /// <summary>
    /// Identity aliases (ADR 0074). An entry refused at bind time is the failure this exists to surface:
    /// the alias is simply absent, so the second account silently lands on an empty dataset, which looks
    /// exactly like the feature never shipped. The startup log says so too, but an operator reading this
    /// tab is not reading logs.
    ///
    /// <para>Re-derives from <see cref="IConfiguration"/> rather than taking the registered
    /// <see cref="UserIdAliases"/> as a parameter, which would make <see cref="BuildStatic"/> impure and
    /// add a mandatory dependency to every host that builds a report (ADR 0029).</para>
    ///
    /// <para><b>What this cannot see.</b> It reports what the <em>configuration</em> would bind, not
    /// whether the map was actually registered in <c>Program.cs</c> — so for the one failure mode where
    /// the singleton is missing entirely, this still reads "active" while the alias does nothing.
    /// <c>ProjectLayoutTests.Program_BuildsAndRegistersTheUserIdAliasMap</c> is the guard for that;
    /// purity (ADR 0029) is worth more here than covering a case a source-text test already pins.</para>
    ///
    /// <para><b>Counts only, never an object id.</b> These ids are committed config rather than secrets,
    /// but the report has no reason to restate them and this tab is readable by every signed-in user.</para>
    /// </summary>
    private static ConfigCheck AliasCheck(IConfiguration config)
    {
        var aliases = UserIdAliases.FromConfiguration(config);

        if (aliases.DroppedEntries.Count > 0)
        {
            return new ConfigCheck(GroupOptional, UserIdAliases.SectionName, Warning,
                $"{aliases.DroppedEntries.Count} entry/entries ignored as invalid and {aliases.Count} "
                + "active — an ignored alias means that account reaches its own empty dataset. See the "
                + "startup log for the reason per entry.", Billable: false);
        }

        return new ConfigCheck(GroupOptional, UserIdAliases.SectionName, Ok,
            aliases.IsEmpty
                ? "no identity aliases configured — every account resolves to its own Entra object id."
                : $"{aliases.Count} alias(es) active: that many additional accounts resolve to another "
                    + "account's data.",
            Billable: false);
    }

    // Who authenticates the caller (ADR 0145). In entra mode the two silent failures are surfaced: an ephemeral Data Protection
    // ring (every restart signs everyone out and orphans stored provider keys) and `local-dev` listed as an owner.
    private static void AddAuthChecks(List<ConfigCheck> checks, IConfiguration config)
    {
        if (AuthModeSetting.Read(config) != AuthMode.Entra)
        {
            checks.Add(new ConfigCheck(GroupInfra, AuthModeSetting.Key, Ok,
                "easyauth — sign-in is done by Azure Container Apps built-in authentication in front of the app (the default).",
                Billable: false));
            return;
        }

        checks.Add(new ConfigCheck(GroupInfra, AuthModeSetting.Key, Ok,
            "entra — the app signs users in itself (Entra OIDC) and stamps the principal headers.", Billable: false));

        var persisted = AuthStartupChecks.DataProtectionRingPersisted(config);
        checks.Add(new ConfigCheck(GroupInfra, "Auth:DataProtectionKeyRing", persisted ? Ok : Error,
            persisted
                ? "persisted — sessions and stored provider keys survive a restart."
                : "ephemeral — every restart signs everyone out and orphans stored provider API keys. Set Storage:BlobEndpoint "
                    + "(not ConnectionStrings:blobs) or use Storage:Provider=local.",
            Billable: false));

        var localDevOwner = AuthStartupChecks.OwnersContainLocalDev(config);
        checks.Add(new ConfigCheck(GroupInfra, "Auth:OwnersLocalDev", localDevOwner ? Error : Ok,
            localDevOwner
                ? "Access:Owners lists 'local-dev'. Remove it: with real sign-in it would make unauthenticated public-path requests owners."
                : "Access:Owners does not list 'local-dev'.",
            Billable: false));
    }

    // The Azure-only keys are not applicable offline, and not applicable when Storage:Provider is local (Postgres plus a
    // volume): there is no blob endpoint to set and no managed identity to point at a tenant.
    private static void AddInfrastructureChecks(List<ConfigCheck> checks, IConfiguration config, bool isDevelopment)
    {
        var provider = StorageProviderSetting.Read(config);
        checks.Add(new ConfigCheck(GroupInfra, StorageProviderSetting.Key, Ok,
            provider == StorageProvider.Local
                ? "local — Postgres and a filesystem volume; no Azure account is used for storage."
                : "azure — Cosmos DB and Blob Storage (the default; DevHost and tests use in-memory twins).",
            Billable: false));

        if (provider == StorageProvider.Local)
        {
            checks.Add(new ConfigCheck(GroupInfra, "Storage:BlobEndpoint", NotApplicable,
                "Not used — the local provider keeps files on a volume.", Billable: false));
            checks.Add(new ConfigCheck(GroupInfra, "AzureOpenAI:TenantId", NotApplicable,
                "Not used — there is no managed identity with local storage.", Billable: false));
            // Local mode throws at startup without it (LocalStorageRegistration); presence only, the value holds a password.
            checks.Add(PresenceCheck(GroupInfra, "ConnectionStrings:postgres", config["ConnectionStrings:postgres"],
                present: "configured", missing: "not set — the local provider cannot start without the Postgres connection string"));
        }
        else if (isDevelopment)
        {
            checks.Add(new ConfigCheck(GroupInfra, "Storage:BlobEndpoint", NotApplicable,
                "Not required offline — the AppHost/DevHost uses an in-memory attachment store.", Billable: false));
            checks.Add(new ConfigCheck(GroupInfra, "AzureOpenAI:TenantId", NotApplicable,
                "Not required offline.", Billable: false));
        }
        else
        {
            checks.Add(UriCheck(GroupInfra, "Storage:BlobEndpoint", config["Storage:BlobEndpoint"],
                requireHttps: true, missingStatus: Warning,
                missingMessage: "not set — blob attachments and the Data Protection key ring degrade"));
            checks.Add(PresenceCheck(GroupInfra, "AzureOpenAI:TenantId", config["AzureOpenAI:TenantId"],
                present: "configured", missing: "not set — the managed identity uses the ambient tenant",
                missingStatus: Warning));
        }
    }
}
