namespace Agent42.Agents;

/// <summary>Refusal codes of the plain-name address lifecycle (ADR 0141); the endpoint maps each to a status.</summary>
public static class EmailErrors
{
    public const string Taken = "taken";
    public const string Reserved = "reserved";
    public const string Empty = "empty";
    public const string NotPlain = "not-plain";
    public const string UnsafeTools = "unsafe-tools";
}

/// <summary>
/// What an update does to an agent's inbound address. <see cref="Token"/>/<see cref="Plain"/> are the values to store;
/// <see cref="RegisterAfterWrite"/> asks the registry to register the token once the state is written (a freshly minted
/// random token, the create/regenerate order); <see cref="Error"/> set means refuse and change nothing.
/// </summary>
public readonly record struct EmailChange(string? Token, bool? Plain, bool RegisterAfterWrite = false, string? Error = null);

/// <summary>
/// The per-agent inbound-address state machine (ADR 0141), one method per transition, extracted from
/// <see cref="AgentRegistryGrain.SaveAsync"/> to keep its cognitive complexity down (ADR 0050/0051).
///
/// <para><b>Ordering is the invariant</b> (gotcha "Two directories now"): a revoked address must never keep resolving.
/// Opt-in and rename are <c>TryClaim(new)</c> (refuse before any change) → <c>Deregister(old)</c> → the registry writes
/// state. A crash after the deregister leaves state pointing at an unregistered address, which fails closed, and the next
/// save of a plain agent re-claims the current slug (<see cref="Heal"/>), so it never stays dead. Opt-out is a
/// regenerate: the slug is deregistered FIRST, then a random token is minted and registered after the write.</para>
///
/// <para>A cross-user conflict is always refused with no call to the holder's registry (two non-reentrant registries
/// calling each other can deadlock). Only a holder that is the SAME user is inspected, through
/// <paramref name="sameUserAgent"/> (never a call to the registry's own grain), and a claim whose agent is gone or no
/// longer holds the slug is taken over with a compare-and-clear.</para>
/// </summary>
public sealed class EmailAddressLifecycle(
    IGrainFactory grains, string userId, Func<string, AgentDefinition?> sameUserAgent)
{
    /// <summary>Decides and performs the directory side of an update for <paramref name="previous"/>.</summary>
    public async Task<EmailChange> ApplyAsync(
        AgentDefinition previous, AgentDefinition incoming, bool? plainEmail, bool regenerateEmail)
    {
        if (previous.EmailPlain == true)
        {
            if (regenerateEmail)
            {
                return Refuse(EmailErrors.NotPlain);
            }

            return plainEmail == false ? await OptOutAsync(previous) : await RenameOrHealAsync(previous, incoming);
        }

        if (plainEmail == true)
        {
            return await OptInAsync(previous, incoming);
        }

        return regenerateEmail ? await RegenerateAsync(previous) : Keep(previous);
    }

    /// <summary>Releases an agent's address on delete. A plain slug is cleared only while this agent still holds it.</summary>
    public async Task ReleaseAsync(AgentDefinition agent)
    {
        if (string.IsNullOrEmpty(agent.EmailToken))
        {
            return;
        }

        var directory = Directory(agent.EmailToken);
        if (agent.EmailPlain == true)
        {
            await directory.DeregisterIfHeldByAsync(userId, agent.Id);
        }
        else
        {
            await directory.DeregisterAsync();
        }
    }

    private async Task<EmailChange> OptInAsync(AgentDefinition previous, AgentDefinition incoming)
    {
        var slug = ValidSlug(incoming.Name, out var error);
        error ??= UnsafeToolsError(previous, incoming);
        if (error is not null)
        {
            return Refuse(error);
        }

        if (!await ClaimAsync(slug!, previous.Id))
        {
            return Refuse(EmailErrors.Taken);
        }

        if (!string.IsNullOrEmpty(previous.EmailToken))
        {
            await Directory(previous.EmailToken).DeregisterAsync();
        }

        return new EmailChange(slug, true);
    }

    private async Task<EmailChange> RenameOrHealAsync(AgentDefinition previous, AgentDefinition incoming)
    {
        var slug = ValidSlug(incoming.Name, out var error);
        error ??= UnsafeToolsError(previous, incoming);
        if (error is not null)
        {
            return Refuse(error);
        }

        if (!await ClaimAsync(slug!, previous.Id))
        {
            return Refuse(EmailErrors.Taken);
        }

        if (!string.Equals(slug, previous.EmailToken, StringComparison.Ordinal))
        {
            await Directory(previous.EmailToken).DeregisterIfHeldByAsync(userId, previous.Id);
        }

        return new EmailChange(slug, true);
    }

    private async Task<EmailChange> OptOutAsync(AgentDefinition previous)
    {
        // The slug dies FIRST; a crash before the write then leaves a plain agent with a dead address, which the next
        // plain save heals, never a live slug on an agent that already shows a random one.
        await Directory(previous.EmailToken).DeregisterIfHeldByAsync(userId, previous.Id);
        return new EmailChange(EmailAddress.Generate().Token, null, RegisterAfterWrite: true);
    }

    private async Task<EmailChange> RegenerateAsync(AgentDefinition previous)
    {
        if (!string.IsNullOrEmpty(previous.EmailToken))
        {
            await Directory(previous.EmailToken).DeregisterAsync();
        }

        return new EmailChange(EmailAddress.Generate().Token, null, RegisterAfterWrite: true);
    }

    private static EmailChange Keep(AgentDefinition previous) => new(previous.EmailToken, previous.EmailPlain);

    private static EmailChange Refuse(string error) => new(null, null, Error: error);

    private static string? ValidSlug(string name, out string? error)
    {
        var slug = PlainEmailName.Slugify(name);
        error = slug is null ? EmailErrors.Empty : PlainEmailName.IsReserved(slug) ? EmailErrors.Reserved : null;
        return slug;
    }

    // The email door runs any stored agent on unauthenticated text once its address is guessable (ADR 0141), so an agent
    // with auto-approved shell tools or a no-delete / cross-chat store reachable cannot be plain. The tool list is the one
    // the save carries; null means "all" (ADR 0021).
    private static string? UnsafeToolsError(AgentDefinition previous, AgentDefinition incoming)
    {
        var tools = incoming.EnabledTools;
        var unsafeKind = AgentKinds.IsCopilot(previous.Kind) || AgentKinds.IsPerChatOnly(previous.Kind);
        var unsafeTools = tools is null || tools.Contains(ToolCatalog.Memory) || tools.Contains(ToolCatalog.PastChats);
        return unsafeKind || unsafeTools ? EmailErrors.UnsafeTools : null;
    }

    // Claims the slug for this agent. A conflict with the same user's own other agent is inspected directly: when that
    // agent is gone or no longer holds the slug, the claim is stale and is taken over by compare-and-clear.
    private async Task<bool> ClaimAsync(string slug, string agentId)
    {
        var directory = Directory(slug);
        if (await directory.TryClaimAsync(userId, agentId))
        {
            return true;
        }

        var holder = await directory.ResolveAsync();
        if (holder is null || holder.UserId != userId)
        {
            return holder is null && await directory.TryClaimAsync(userId, agentId);
        }

        var holderAgent = sameUserAgent(holder.AgentId);
        var stale = holderAgent is null || !string.Equals(holderAgent.EmailToken, slug, StringComparison.Ordinal);
        if (!stale)
        {
            return false;
        }

        await directory.DeregisterIfHeldByAsync(userId, holder.AgentId);
        return await directory.TryClaimAsync(userId, agentId);
    }

    private IEmailAddressDirectoryGrain Directory(string? token)
        => grains.GetGrain<IEmailAddressDirectoryGrain>(EmailAddress.Hash(token ?? ""));
}
