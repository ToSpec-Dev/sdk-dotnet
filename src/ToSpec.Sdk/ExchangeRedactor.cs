using System.Buffers;
using Microsoft.AspNetCore.Http;
using ToSpec.Redact;
using ToSpec.Sdk.Wire;

namespace ToSpec.Sdk;

/// <summary>The raw material captured on the request thread for one exchange, before
/// redaction. Body byte arrays are bounded copies; header references are live for the
/// duration of the redaction call (which runs in the middleware's finally, right after
/// the response completes). This never enters the background queue — the queue only ever
/// holds the <see cref="IngestEventEnvelope"/> produced from it, so raw bytes never leave
/// the request thread.</summary>
internal sealed class CapturedExchange
{
    public required Guid EventId { get; init; }

    public required Guid PartnerId { get; init; }

    public required DateTimeOffset Ts { get; init; }

    public required string Method { get; init; }

    public required string Path { get; init; }

    public required int? Status { get; init; }

    public required int LatencyMs { get; init; }

    public required IHeaderDictionary RequestHeaders { get; init; }

    public required IHeaderDictionary ResponseHeaders { get; init; }

    public byte[]? RequestBody { get; init; }

    public byte[]? ResponseBody { get; init; }

    public string? RequestContentType { get; init; }

    public string? ResponseContentType { get; init; }

    public int RequestSize { get; init; }

    public int ResponseSize { get; init; }
}

/// <summary>
/// Turns a <see cref="CapturedExchange"/> into a redacted <see cref="IngestEventEnvelope"/>
/// — the redaction-before-transmission step (SPEC-cert-gateway-architecture §9), applied
/// locally with the fetched ruleset. Headers are stripped/hashed; each body is redacted by
/// its own format's engine over pooled buffers. The safety policy is conservative in every
/// failure mode: no ruleset yet, an unstructured format, or a malformed body all mean the
/// body is <b>dropped</b>, never transmitted raw.
/// </summary>
internal static class ExchangeRedactor
{
    public static IngestEventEnvelope Redact(
        CapturedExchange exchange, ConformanceSnapshot snapshot, RedactionKeyring keys, ConformanceMetrics metrics)
    {
        CompiledRuleset? rules = snapshot.Ruleset;

        Dictionary<string, string> reqHeaders =
            HeaderRedactor.Redact(exchange.RequestHeaders, isRequest: true, rules, keys);
        Dictionary<string, string> respHeaders =
            HeaderRedactor.Redact(exchange.ResponseHeaders, isRequest: false, rules, keys);

        string reqFormat = ContentFormat.Detect(exchange.RequestContentType);
        string respFormat = ContentFormat.Detect(exchange.ResponseContentType);

        string? reqBody = RedactBody(exchange.RequestBody, reqFormat, snapshot, keys, metrics);
        string? respBody = RedactBody(exchange.ResponseBody, respFormat, snapshot, keys, metrics);

        // Protocol v1 has one content_format for both optional bodies. Mixed retained
        // formats cannot be represented without mislabelling one body, so deterministically
        // retain the request body and drop the response.
        bool mixedFormats = reqBody is not null && respBody is not null && reqFormat != respFormat;
        string? safeRespBody = mixedFormats ? null : respBody;
        string contentFormat = reqBody is not null
            ? reqFormat
            : safeRespBody is not null ? respFormat : ContentFormat.Json;

        return new IngestEventEnvelope
        {
            EventId = exchange.EventId,
            PartnerId = exchange.PartnerId,
            Ts = exchange.Ts,
            Direction = "inbound",
            Method = exchange.Method,
            Path = exchange.Path,
            Status = exchange.Status,
            LatencyMs = exchange.LatencyMs,
            ReqHeaders = reqHeaders,
            RespHeaders = respHeaders,
            ReqBody = reqBody,
            RespBody = safeRespBody,
            ReqSize = exchange.RequestSize,
            RespSize = exchange.ResponseSize,
            ContentFormat = contentFormat,
            RedactionVersion = snapshot.RulesetVersion,
        };
    }

    private static string? RedactBody(
        byte[]? raw, string format, ConformanceSnapshot snapshot, RedactionKeyring keys, ConformanceMetrics metrics)
    {
        if (raw is null || raw.Length == 0)
        {
            return null;
        }

        // Fail-safe: with no compiled ruleset we cannot redact, so we never transmit a body.
        if (snapshot.Ruleset is null)
        {
            return null;
        }

        IBodyRedactor? redactor = BodyRedactorRegistry.Resolve(format);
        if (redactor is null)
        {
            // text/binary have no structured redactor — drop rather than transmit raw.
            return null;
        }

        var input = new ReadOnlySequence<byte>(raw);
        var output = new ArrayBufferWriter<byte>(raw.Length + 256);
        RedactionResult result = redactor.Redact(input, output, snapshot.Ruleset, keys);
        if (result.Status != RedactionStatus.Rewritten)
        {
            // Malformed body: the engine's output is undefined — drop it.
            metrics.IncRedactionFailure();
            return null;
        }

        return Convert.ToBase64String(output.WrittenSpan);
    }
}
