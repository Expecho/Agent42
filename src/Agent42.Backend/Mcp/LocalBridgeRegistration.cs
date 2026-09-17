namespace Agent42.Backend;

/// <summary>
/// Wires the local bridge (ADR 0076) — lifted out of <c>Program.cs</c> in the
/// <see cref="AgentMcpServerRegistration"/> shape, and called by <b>both</b> silo hosts.
///
/// <para><b>Deliberately NOT an <c>AgentApi.*</c> partial</b>, for ADR 0070's reason: a public,
/// self-enforcing route stays out of the Easy-Auth composition root that ten hosts map.</para>
/// </summary>
public static class LocalBridgeRegistration
{
    /// <summary>
    /// Registers the registry and decorates the <see cref="IMcpConnectionFactory"/> already registered.
    /// <b>Call it after that registration</b> — it wraps whichever one is last, and throws if there is none
    /// (a silent no-op would ship every bridge server as permanently "not connected").
    /// </summary>
    public static void AddLocalBridge(this IServiceCollection services)
    {
        services.AddSingleton(_ => new LocalBridgeRegistry());

        var inner = services.LastOrDefault(d => d.ServiceType == typeof(IMcpConnectionFactory))
            ?? throw new InvalidOperationException(
                "AddLocalBridge decorates IMcpConnectionFactory; register the inner factory first.");
        services.Remove(inner);
        services.AddSingleton<IMcpConnectionFactory>(sp => new LocalBridgeConnectionFactory(
            Resolve(sp, inner),
            sp.GetRequiredService<LocalBridgeRegistry>(),
            sp.GetRequiredService<ILoggerFactory>()));
    }

    /// <summary>
    /// Enables WebSockets and maps <see cref="LocalBridgeEndpoint.Route"/>. Only <c>UseWebSockets</c> is
    /// order-sensitive — it is middleware and must run before the endpoint executes; the route itself is an
    /// endpoint, so it runs at the end of the pipeline wherever this is called. Kept beside the other
    /// excluded-path wiring in both hosts so the public surface reads in one place.
    ///
    /// <para><c>Map</c>, not <c>MapGet</c>, on purpose: a WebSocket over HTTP/2 (RFC 8441) arrives as
    /// <c>CONNECT</c>, and a later "tidy-up" to <c>MapGet</c> would silently break that. Every method still
    /// meets the same fail-closed key check first.</para>
    /// </summary>
    public static void UseLocalBridge(this WebApplication app)
    {
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = LocalBridgeLimits.KeepAliveInterval,
            KeepAliveTimeout = LocalBridgeLimits.KeepAliveTimeout,
        });
        app.Map(LocalBridgeEndpoint.Route, LocalBridgeEndpoint.HandleAsync);
    }

    private static IMcpConnectionFactory Resolve(IServiceProvider sp, ServiceDescriptor inner) =>
        (IMcpConnectionFactory)(inner.ImplementationInstance
            ?? inner.ImplementationFactory?.Invoke(sp)
            ?? ActivatorUtilities.CreateInstance(sp, inner.ImplementationType!));
}
