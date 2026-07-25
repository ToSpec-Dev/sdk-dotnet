namespace ToSpec.Sdk;

/// <summary>
/// Lock-free counters for the conformance pipeline. Every degradation the SDK chooses
/// (dropped-on-backpressure, redaction failures, rejected batches, kill switch) is a
/// counter here rather than an exception — the guarantees are observable, not silent.
/// The host reads <see cref="Snapshot"/> to export these to its own metrics system.
/// </summary>
public sealed class ConformanceMetrics
{
    private long _eventsCaptured;
    private long _eventsSampledOut;
    private long _eventsDroppedQueueFull;
    private long _batchesSent;
    private long _batchesFailed;
    private long _eventsIngested;
    private long _redactionFailures;
    private long _configPolls;
    private long _configChanges;
    private long _configNotModified;
    private int _killSwitchActive;

    internal void IncCaptured() => Interlocked.Increment(ref _eventsCaptured);

    internal void IncSampledOut() => Interlocked.Increment(ref _eventsSampledOut);

    /// <summary>Fired by the bounded channel's drop-oldest callback — one per evicted event.</summary>
    internal void IncDroppedQueueFull() => Interlocked.Increment(ref _eventsDroppedQueueFull);

    internal void IncBatchSent(int events)
    {
        Interlocked.Increment(ref _batchesSent);
        Interlocked.Add(ref _eventsIngested, events);
    }

    internal void IncBatchFailed() => Interlocked.Increment(ref _batchesFailed);

    internal void IncRedactionFailure() => Interlocked.Increment(ref _redactionFailures);

    internal void IncConfigPoll() => Interlocked.Increment(ref _configPolls);

    internal void IncConfigChange() => Interlocked.Increment(ref _configChanges);

    internal void IncConfigNotModified() => Interlocked.Increment(ref _configNotModified);

    internal void SetKillSwitch(bool active) => Interlocked.Exchange(ref _killSwitchActive, active ? 1 : 0);

    /// <summary>A consistent-enough point-in-time read of every counter.</summary>
    public ConformanceMetricsSnapshot Snapshot() => new(
        EventsCaptured: Interlocked.Read(ref _eventsCaptured),
        EventsSampledOut: Interlocked.Read(ref _eventsSampledOut),
        EventsDroppedQueueFull: Interlocked.Read(ref _eventsDroppedQueueFull),
        BatchesSent: Interlocked.Read(ref _batchesSent),
        BatchesFailed: Interlocked.Read(ref _batchesFailed),
        EventsIngested: Interlocked.Read(ref _eventsIngested),
        RedactionFailures: Interlocked.Read(ref _redactionFailures),
        ConfigPolls: Interlocked.Read(ref _configPolls),
        ConfigChanges: Interlocked.Read(ref _configChanges),
        ConfigNotModified: Interlocked.Read(ref _configNotModified),
        KillSwitchActive: Interlocked.CompareExchange(ref _killSwitchActive, 0, 0) == 1);
}

/// <summary>Immutable snapshot of <see cref="ConformanceMetrics"/>.</summary>
public readonly record struct ConformanceMetricsSnapshot(
    long EventsCaptured,
    long EventsSampledOut,
    long EventsDroppedQueueFull,
    long BatchesSent,
    long BatchesFailed,
    long EventsIngested,
    long RedactionFailures,
    long ConfigPolls,
    long ConfigChanges,
    long ConfigNotModified,
    bool KillSwitchActive);
