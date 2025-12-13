using System.Diagnostics.Metrics;

namespace CLOOPS.NATS;

/// <summary>
/// Service for recording NATS-related metrics using System.Diagnostics.Metrics.
/// Provides functionality to track message processing times and other NATS operation metrics.
/// </summary>
public interface INatsMetricsService
{
    /// <summary>
    /// Gets the histogram metric for tracking NATS subscription message processing time.
    /// Time taken to process a nats subscription message.
    /// Labels: fn (function name), status (success/fail), retryable (true/false)
    /// Histogram automatically tracks counts, so we can get message counts by filtering on labels.
    /// This automatically creates three metrics:
    /// nats_sub_msg_process_milliseconds_count,
    /// nats_sub_msg_process_milliseconds_sum,
    /// nats_sub_msg_process_milliseconds_bucket for quantile
    /// </summary>
    Histogram<double> natsSubMsgProcessMs { get; }

    /// <summary>
    /// Records the duration (in milliseconds) for processing a NATS subscription message.
    /// </summary>
    /// <param name="duration">The duration in milliseconds that the message processing took.</param>
    /// <param name="fn">The function name that processed the message.</param>
    /// <param name="status">Execution status</param>
    /// <param name="retryable">Whether the message processing failure is retryable.</param>
    void RecordNatsSubMsgProcessMs(double duration, string fn, string status, bool retryable);
}

/// <inheritdoc cref="INatsMetricsService"/>
public class NatsMetricsService : INatsMetricsService
{
    private readonly Meter _meter;

    // best practice is to use milliseconds as duration units
    // common metrics (will be moved to SDK later)

    /// <inheritdoc/>
    public Histogram<double> natsSubMsgProcessMs { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="NatsMetricsService"/> class.
    /// Creates a new meter named "NatsMetrics" and initializes the subscription message processing histogram.
    /// </summary>
    public NatsMetricsService()
    {
        _meter = new Meter("NatsMetrics");

        natsSubMsgProcessMs = _meter.CreateHistogram<double>(
            "nats_sub_msg_process",
            "ms",
            "Time taken to process a nats subscription message. Labels: fn, status, retryable"
        );
    }

    /// <inheritdoc/>
    public void RecordNatsSubMsgProcessMs(double duration, string fn, string status, bool retryable)
    {
        KeyValuePair<string, object?>[] tags =
        [
            new("fn", fn),
            new("status", status),
            new("retryable", retryable.ToString().ToLowerInvariant())
        ];
        natsSubMsgProcessMs.Record(duration, tags.AsSpan());
    }
}