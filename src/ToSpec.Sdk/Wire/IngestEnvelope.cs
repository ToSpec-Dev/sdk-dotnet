namespace ToSpec.Sdk.Wire;

/// <summary>
/// The wire shape of one ingest batch POST body (gzip of this JSON, snake_case). This
/// is the SDK-side twin of the platform's <c>ToSpec.Ingest.Ingest.IngestBatch</c> — the
/// SDK owns its own copy of the contract (it cannot reference the platform host), and
/// the <c>ToSpec-Dev/sdk-protocol</c> golden fixtures pin the two together. Bodies are
/// already redacted client-side (SPEC-cert-gateway-architecture §5.1) and carried
/// base64-encoded; <c>tenant_id</c>/<c>api_id</c> are never sent (the server derives
/// them from the ingest key).
/// </summary>
public sealed record IngestBatch
{
    public Guid BatchId { get; init; }

    public IReadOnlyList<IngestEventEnvelope> Events { get; init; } = [];
}

/// <summary>One captured request/response exchange in a batch. Field-for-field the
/// server's envelope; see <c>PROTOCOL.md</c> in <c>ToSpec-Dev/sdk-protocol</c>.</summary>
public sealed record IngestEventEnvelope
{
    public Guid EventId { get; init; }

    public Guid PartnerId { get; init; }

    public DateTimeOffset Ts { get; init; }

    /// <summary>'inbound' | 'outbound'. The middleware captures inbound traffic to the
    /// provider's own API, so this is always 'inbound' here.</summary>
    public string Direction { get; init; } = "inbound";

    public string Method { get; init; } = "";

    public string Path { get; init; } = "";

    public int? Status { get; init; }

    public int? LatencyMs { get; init; }

    public IReadOnlyDictionary<string, string>? ReqHeaders { get; init; }

    public IReadOnlyDictionary<string, string>? RespHeaders { get; init; }

    /// <summary>Base64 of the already-redacted request body; null/absent when none or dropped.</summary>
    public string? ReqBody { get; init; }

    public string? RespBody { get; init; }

    /// <summary>Original (pre-redaction) request body size, when known.</summary>
    public int ReqSize { get; init; }

    public int RespSize { get; init; }

    /// <summary>'json' | 'xml' | 'text' | 'binary'.</summary>
    public string ContentFormat { get; init; } = "json";

    /// <summary>The ruleset version the SDK applied; 0 = no ruleset compiled yet.</summary>
    public int RedactionVersion { get; init; }
}
