namespace Agent42.Agents;

/// <summary>
/// The one global list of chat connections the gateway should run (ADR 0111), so it can start every bot at boot
/// without scanning every user. Keyed by <see cref="Key"/>; there is exactly one activation.
///
/// <para>It holds no credential and resolves nothing by secret, so unlike the email and share directories it is not a
/// credential namespace: knowing an entry grants nothing. <see cref="UserChatConnectionsGrain"/> keeps it in sync and
/// removes an entry <b>before</b> deleting or disabling the connection, so a gateway restart racing a delete can never
/// revive a session.</para>
/// </summary>
[Alias("Agent42.Agents.IChatConnectionDirectoryGrain")]
public interface IChatConnectionDirectoryGrain : IGrainWithStringKey
{
    /// <summary>The single grain key.</summary>
    public const string Key = "all";

    /// <summary>Adds (or keeps) <paramref name="userId"/>/<paramref name="connectionId"/>. Idempotent.</summary>
    [Alias("RegisterAsync")]
    Task RegisterAsync(string userId, string connectionId);

    /// <summary>Removes the entry. Idempotent.</summary>
    [Alias("DeregisterAsync")]
    Task DeregisterAsync(string userId, string connectionId);

    /// <summary>Every registered entry.</summary>
    [Alias("ListAsync")]
    Task<IReadOnlyList<ChatConnectionRef>> ListAsync();
}

/// <summary>Points at one user's connection.</summary>
[GenerateSerializer]
[Alias("Agent42.Agents.ChatConnectionRef")]
public sealed record ChatConnectionRef(
    [property: Id(0)] string UserId,
    [property: Id(1)] string ConnectionId);
