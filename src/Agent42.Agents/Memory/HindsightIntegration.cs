using Hindsight.AgentFramework;

namespace Agent42.Agents;

/// <summary>
/// The host's long-term-memory wiring (ADR 0056): the Hindsight client to use, or <c>null</c> when no
/// <c>Hindsight:ApiKey</c> is configured and memory is therefore off.
/// </summary>
/// <remarks>
/// <para>There is deliberately no <c>IMemoryService</c> port and no Backend adapter folder here, unlike
/// <see cref="IGeocoder"/> or <see cref="IChatSearchIndex"/>. The package's <c>IHindsightClient</c> already
/// <em>is</em> that seam — it is an interface over the third party, faked in tests exactly as a port would
/// be — so an Agent42 interface wrapping it would be a second abstraction for the same thing.</para>
/// <para>The null carries meaning and is the operator's off switch: with no key, <c>BuildAgent</c> attaches
/// no memory provider at all, so a turn makes zero Hindsight calls rather than calls that quietly fail.
/// This is a <b>mandatory</b> grain dependency all the same — every silo must register it, or activation
/// fails — because "memory is off" must be a registered decision, not a missing registration.</para>
/// </remarks>
/// <param name="Client">The Hindsight client, or null when memory is disabled for this deployment.</param>
/// <param name="Mission">
/// Optional bank mission (<c>Hindsight:Mission</c>), used to create a user's bank on first use. Null means
/// the bank is assumed to exist and no create call is ever made.
/// </param>
public sealed record HindsightIntegration(IHindsightClient? Client, string? Mission = null);
