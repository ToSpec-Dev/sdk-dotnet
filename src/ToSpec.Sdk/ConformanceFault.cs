namespace ToSpec.Sdk;

/// <summary>Which subsystem a swallowed fault came from.</summary>
public enum ConformanceFaultKind
{
    /// <summary>A config poll (<c>GET /v1/sdk/config</c>) failed; the last-good snapshot is kept.</summary>
    ConfigPoll,

    /// <summary>A batch send (<c>POST /v1/ingest</c>) failed or was rejected; the batch is dropped.</summary>
    BatchSend,

    /// <summary>Redaction or envelope construction failed for one exchange; that event is dropped.</summary>
    Redaction,

    /// <summary>Capturing an exchange (partner resolution, body buffering) failed; the host is unaffected.</summary>
    Capture,
}

/// <summary>
/// The single logging hook the SDK surfaces faults through (SPEC-cert-gateway-architecture
/// §9: "zero user-visible failures — all faults swallowed"). The SDK never throws into the
/// host request pipeline; every fault is reported here (and counted in
/// <see cref="ConformanceMetrics"/>) so the host can log or alert without the SDK ever
/// affecting request handling.
/// </summary>
public sealed record ConformanceFault(ConformanceFaultKind Kind, string Message, Exception? Exception);
