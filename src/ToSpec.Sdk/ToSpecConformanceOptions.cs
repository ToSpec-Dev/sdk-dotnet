using Microsoft.AspNetCore.Http;

namespace ToSpec.Sdk;

/// <summary>
/// Configuration for <c>UseToSpecConformance</c>. The required values (ingest endpoint,
/// ingest key, redaction key, partner resolver) come from the provider's ToSpec portal;
/// the tuning knobs have production-safe defaults. The redaction key is supplied here
/// rather than fetched from the config endpoint because it is a tenant secret the provider
/// already holds — using the same key the gateway uses makes prod <c>tsr_v{n}_…</c> tokens
/// join the certification path's tokens.
/// </summary>
public sealed class ToSpecConformanceOptions
{
    /// <summary>Base address of the ingest edge, e.g. <c>https://ingest.tospec.net</c>.
    /// Both <c>/v1/ingest</c> and <c>/v1/sdk/config</c> are resolved against it.</summary>
    public Uri? IngestBaseUrl { get; set; }

    /// <summary>The per-tenant ingest key (<c>tsp_ing_…</c>): the bearer credential and the
    /// HMAC secret for the batch signature.</summary>
    public string? IngestKey { get; set; }

    /// <summary>The per-tenant redaction HMAC key (raw bytes) used to tokenize hashed
    /// values. Same key the gateway uses ⇒ tokens join across the cert and prod paths.</summary>
#pragma warning disable CA1819 // Byte-array key material is deliberately exposed as-is; wrapping it adds no safety.
    public byte[]? RedactionKey { get; set; }
#pragma warning restore CA1819

    /// <summary>Version of <see cref="RedactionKey"/>, embedded in every emitted token
    /// (<c>tsr_v{version}_…</c>). Must be ≥ 1.</summary>
    public int RedactionKeyVersion { get; set; } = 1;

    /// <summary>Resolves the partner identity for a request (which counterparty is calling
    /// the provider's API). Returning null skips capture for that request. Provider-specific
    /// — e.g. mapped from an API key, mTLS cert, or JWT claim.</summary>
    public Func<HttpContext, Guid?>? ResolvePartnerId { get; set; }

    /// <summary>Capture request bodies (redacted). Default true.</summary>
    public bool CaptureRequestBodies { get; set; } = true;

    /// <summary>Capture response bodies (redacted). Default true.</summary>
    public bool CaptureResponseBodies { get; set; } = true;

    /// <summary>Max bytes copied from each body for capture (a per-exchange bound on the
    /// work done on the request thread). Default 64 KiB.</summary>
    public int MaxBodyBytes { get; set; } = 64 * 1024;

    /// <summary>Max events held in the background queue before drop-oldest kicks in — the
    /// hard memory bound. Default 10,000.</summary>
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>Maximum estimated bytes retained by queued redacted events. This is
    /// enforced independently from <see cref="QueueCapacity"/>. Default 64 MiB.</summary>
    public long MaxQueueBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Max events per POSTed batch. Default 200.</summary>
    public int MaxBatchEvents { get; set; } = 200;

    /// <summary>Soft cap on a batch's pre-gzip JSON size before it is flushed. Default 4 MiB.</summary>
    public int MaxBatchBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Max time the sender lingers accumulating a partial batch before flushing it.
    /// Default 5s.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the SDK polls <c>GET /v1/sdk/config</c>. The kill switch takes
    /// effect within one interval. Default 15s.</summary>
    public TimeSpan ConfigPollInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Per-request timeout for ingest POSTs and config GETs. A hung endpoint can
    /// never exceed this on the background threads, and never touches the request thread.
    /// Default 10s.</summary>
    public TimeSpan IngestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Optional logging hook for swallowed faults (SPEC §9). Never throws are
    /// surfaced to the host pipeline; they arrive here instead.</summary>
    public Action<ConformanceFault>? OnFault { get; set; }

    /// <summary>Validates required values and ranges; throws
    /// <see cref="InvalidOperationException"/> with a specific message on misconfiguration.</summary>
    public void Validate()
    {
        if (IngestBaseUrl is null)
        {
            throw new InvalidOperationException($"{nameof(IngestBaseUrl)} is required.");
        }

        if (string.IsNullOrWhiteSpace(IngestKey))
        {
            throw new InvalidOperationException($"{nameof(IngestKey)} is required.");
        }

        if (RedactionKey is null || RedactionKey.Length == 0)
        {
            throw new InvalidOperationException($"{nameof(RedactionKey)} is required.");
        }

        if (RedactionKeyVersion < 1)
        {
            throw new InvalidOperationException($"{nameof(RedactionKeyVersion)} must be ≥ 1.");
        }

        if (ResolvePartnerId is null)
        {
            throw new InvalidOperationException(
                $"{nameof(ResolvePartnerId)} is required (it maps a request to the calling partner).");
        }

        ThrowIfNotPositive(MaxBodyBytes, nameof(MaxBodyBytes));
        ThrowIfNotPositive(QueueCapacity, nameof(QueueCapacity));
        if (MaxQueueBytes <= 0)
        {
            throw new InvalidOperationException($"{nameof(MaxQueueBytes)} must be positive.");
        }
        ThrowIfNotPositive(MaxBatchEvents, nameof(MaxBatchEvents));
        ThrowIfNotPositive(MaxBatchBytes, nameof(MaxBatchBytes));

        if (ConfigPollInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(ConfigPollInterval)} must be positive.");
        }

        if (IngestTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(IngestTimeout)} must be positive.");
        }
    }

    private static void ThrowIfNotPositive(int value, string name)
    {
        if (value <= 0)
        {
            throw new InvalidOperationException($"{name} must be positive.");
        }
    }
}
