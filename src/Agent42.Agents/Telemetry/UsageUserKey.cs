namespace Agent42.Agents;

/// <summary>
/// The usage rollups' display key for a user (ADR 0031): email, else name, else id. The ONE definition, used
/// by <see cref="UsageSpanProjector"/> when it writes a bucket and by the <c>/api/usage</c> endpoint when it
/// matches a pre-<see cref="TokenBucket.UserId"/> bucket for an owner, so the write and the read cannot drift
/// (a drift shows the user an empty report; gotchas: the same function feeds the stamp and the filter).
///
/// <para><b>A key, never an identity.</b> An email claim is mutable and not unique across tenants (ADR 0121),
/// so scoping a report by it would let a foreign-tenant admin with a spoofed UPN read someone else's usage.
/// Reports are scoped by the object id (<see cref="UsageEvent.UserId"/>); this key only labels rows and
/// matches legacy buckets, which predate the id.</para>
/// </summary>
public static class UsageUserKey
{
    /// <summary>First non-empty of <paramref name="email"/>, <paramref name="name"/>, <paramref name="id"/>; else <c>""</c>.</summary>
    public static string From(string? email, string? name, string? id)
        => !string.IsNullOrEmpty(email) ? email
            : !string.IsNullOrEmpty(name) ? name
            : id ?? "";
}
