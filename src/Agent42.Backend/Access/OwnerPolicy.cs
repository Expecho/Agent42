namespace Agent42.Backend;

/// <summary>
/// Who counts as an owner (ADR 0121): the one question every owner-only surface asks, answered in one
/// place. Owners are the people who run the deployment; everyone else who can sign in is a plain user.
///
/// <para><b>Advisory, not a security boundary.</b> Any signed-in user can run commands in the service's own
/// environment through the Copilot shell (ADR 0107), and can reach the app's localhost through user-supplied
/// MCP and provider endpoints, so an adversarial user can forge the principal header and become an owner.
/// The tier stops accidental and casual access to <c>/dashboard</c>, <c>/logs</c>, config health, other
/// users' usage and prompt text in telemetry. It does not stop a determined user. ADR 0121 says so in those
/// words; do not describe this as isolation anywhere.</para>
/// </summary>
public interface IOwnerPolicy
{
    /// <summary>
    /// Whether <paramref name="userId"/> is an owner. Pass the <b>post-alias</b> id
    /// (<see cref="CurrentUser.GetUserId"/>, ADR 0074), so an aliased account is the same person.
    /// </summary>
    bool IsOwner(string? userId);
}

/// <summary>
/// <see cref="IOwnerPolicy"/> over the committed <c>Access:Owners</c> list of Entra object ids.
///
/// <para><b>Fails closed.</b> An empty list, a missing section, or a blank <paramref name="userId"/> denies
/// everyone; a missing configuration value must never widen access.</para>
///
/// <para><b>Compared <see cref="StringComparison.OrdinalIgnoreCase"/>.</b> An object id is a GUID, and one
/// pasted in different casing is the same principal; an ordinal compare would 404 the dashboard for its own
/// owner, which looks exactly like the feature being broken.</para>
///
/// <para><b>Never name <c>local-dev</c> in shipped configuration.</b> It is <c>CurrentUser.GetUserId</c>'s
/// fallback for a request with no Easy Auth principal header, so listing it in
/// <c>src/Agent42.Backend/appsettings.json</c> would make every unauthenticated request an owner. DevHost and
/// the AppHost set it in code (they have no Easy Auth); <c>DashboardOwnerGateTests</c> pins the shipped file.</para>
/// </summary>
public sealed class OwnerPolicy(IEnumerable<string?> owners) : IOwnerPolicy
{
    /// <summary>Configuration section holding the owner object ids (an array of GUID strings).</summary>
    public const string SectionName = "Access:Owners";

    private readonly HashSet<string> _owners = new(
        owners.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!.Trim()),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>A policy with no owners: nobody is one. The state a host with nothing configured is in.</summary>
    public static OwnerPolicy None { get; } = new([]);

    /// <summary>Reads <see cref="SectionName"/>. Comment keys (<c>//…</c>) are not array items, so are ignored.</summary>
    public static OwnerPolicy FromConfiguration(IConfiguration configuration)
        => new(configuration.GetSection(SectionName).GetChildren().Select(child => child.Value));

    /// <inheritdoc />
    public bool IsOwner(string? userId)
        => !string.IsNullOrWhiteSpace(userId) && _owners.Contains(userId.Trim());
}
