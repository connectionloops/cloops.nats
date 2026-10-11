using Microsoft.Extensions.Logging;
using NATS.Client.Core;

namespace CLOOPS.NATS;

/// <summary>
/// Optional connection settings for <see cref="CloopsNatsClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every property is optional. A property left <c>null</c> keeps the value <see cref="CloopsNatsClient"/> has
/// always used (the NATS.Net default), so constructing a client without an options object, or with
/// <c>new CloopsNatsClientOptions()</c>, behaves exactly as it did before this type existed.
/// </para>
/// <para>
/// The timeouts matter most for services that publish to JetStream from request paths: a JetStream publish
/// waits for the server's acknowledgement for at most <see cref="RequestTimeout"/> (NATS.Net default: 5s)
/// and surfaces a miss as <c>NatsJSPublishNoResponseException</c>, even when the server did reply but the
/// process was too busy (thread-pool starvation, GC pause) to observe the reply in time.
/// </para>
/// </remarks>
public sealed class CloopsNatsClientOptions
{
    /// <summary>
    /// Maximum time to wait for a reply to a request, including JetStream publish acknowledgements.
    /// NATS.Net default: 5 seconds. Services publishing to JetStream from request paths should use 15-30 seconds.
    /// </summary>
    public TimeSpan? RequestTimeout { get; init; }

    /// <summary>
    /// Maximum time to wait for a protocol command (publish, subscribe, flush) to be written to the socket.
    /// NATS.Net default: 5 seconds.
    /// </summary>
    public TimeSpan? CommandTimeout { get; init; }

    /// <summary>
    /// Maximum time to wait for the TCP/TLS connection to be established. NATS.Net default: 2 seconds.
    /// </summary>
    public TimeSpan? ConnectTimeout { get; init; }

    /// <summary>
    /// Logger factory handed to NATS.Net so its own connect, reconnect, ping and retry diagnostics become
    /// visible. NATS.Net default: <c>NullLoggerFactory</c>, which discards everything. Always set this in services.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// When <see cref="LoggerFactory"/> is set, also log the connection lifecycle events
    /// (<c>ConnectionOpened</c>, <c>ConnectionDisconnected</c>, <c>ReconnectFailed</c>, <c>MessageDropped</c>,
    /// <c>LameDuckModeActivated</c>) under the <see cref="CloopsNatsClient"/> category. Default: <c>true</c>.
    /// Set to <c>false</c> if the application subscribes to those events itself.
    /// </summary>
    public bool LogConnectionEvents { get; init; } = true;

    /// <summary>
    /// Escape hatch applied last: receives the fully built <see cref="NatsOpts"/> and returns the one to use.
    /// <see cref="NatsOpts"/> is an init-only record, so use a <c>with</c> expression:
    /// <c>Configure = o => o with { MaxReconnectRetry = 10 }</c>. Returning <c>null</c> keeps the options unchanged.
    /// </summary>
    public Func<NatsOpts, NatsOpts>? Configure { get; init; }

    /// <summary>
    /// The options used when none are supplied: every property unset, which preserves NATS.Net defaults.
    /// </summary>
    public static CloopsNatsClientOptions Default { get; } = new();
}
