using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using ToSpec.Redact;
using ToSpec.Sdk.Wire;

namespace ToSpec.Sdk;

/// <summary>
/// The conformance middleware (SPEC-cert-gateway-architecture §9). For each request it
/// clones metadata and (optionally) bounded body copies, redacts them locally with the
/// fetched ruleset, and hands the redacted envelope to the background queue. The hard
/// guarantees are structural here: the kill switch is the cheapest possible check and
/// passes the request through untouched; the only work added to the request path is a
/// bounded memory copy plus a fast synchronous redaction (no network, no awaiting I/O);
/// enqueue is non-blocking drop-oldest; and every capture fault is swallowed in a filtered
/// catch so nothing the SDK does can fail the host's request.
/// </summary>
internal sealed class ToSpecConformanceMiddleware(
    RequestDelegate next,
    ToSpecConformanceOptions options,
    ConformanceState state,
    ConformanceChannel channel,
    ConformanceMetrics metrics,
    RedactionKeyring keys,
    Sampler sampler)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ConformanceSnapshot snapshot = state.Current;

        // Kill switch: the cheapest path — pass through with zero capture work.
        if (snapshot.KillSwitch)
        {
            await next(context);
            return;
        }

        Guid? partner = ResolvePartner(context);
        if (partner is not { } partnerId || partnerId == Guid.Empty)
        {
            await next(context);
            return;
        }

        Stream? originalRequestBody = null;
        ReadTeeStream? requestTee = null;
        if (options.CaptureRequestBodies)
        {
            originalRequestBody = context.Request.Body;
            requestTee = new ReadTeeStream(originalRequestBody, options.MaxBodyBytes);
            context.Request.Body = requestTee;
        }

        Stream? originalBody = null;
        TeeStream? tee = null;
        if (options.CaptureResponseBodies)
        {
            originalBody = context.Response.Body;
            tee = new TeeStream(originalBody, options.MaxBodyBytes);
            context.Response.Body = tee;
        }

        long startTicks = Stopwatch.GetTimestamp();
        DateTimeOffset ts = DateTimeOffset.UtcNow;
        bool faulted = false;
        try
        {
            await next(context);
        }
        catch
        {
            faulted = true;
            throw;
        }
        finally
        {
            if (requestTee is not null && originalRequestBody is not null)
            {
                context.Request.Body = originalRequestBody;
            }

            if (tee is not null && originalBody is not null)
            {
                context.Response.Body = originalBody;
            }

            long latencyMs = (long)Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;

            // A thrown endpoint unwinds through here BEFORE an outer UseExceptionHandler
            // turns it into a 5xx (that handler sits above this middleware), so
            // Response.StatusCode is still the default 200. Record it as a 500 so error
            // sampling keeps the failure and the event carries the right status.
            int status = context.Response.StatusCode;
            if (faulted && status < 400)
            {
                status = StatusCodes.Status500InternalServerError;
            }

            byte[] requestBytes = requestTee?.CapturedBytes ?? [];
            CaptureSafely(
                context, partnerId, ts, latencyMs,
                requestBytes.Length == 0 ? null : requestBytes,
                tee, snapshot, status);
            requestTee?.Dispose();
            tee?.Dispose();
        }
    }

    private Guid? ResolvePartner(HttpContext context)
    {
        try
        {
            return options.ResolvePartnerId?.Invoke(context);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FaultReporter.Report(options, new ConformanceFault(ConformanceFaultKind.Capture, ex.Message, ex));
            return null;
        }
    }

    private void CaptureSafely(
        HttpContext context,
        Guid partnerId,
        DateTimeOffset ts,
        long latencyMs,
        byte[]? requestBody,
        TeeStream? tee,
        ConformanceSnapshot snapshot,
        int status)
    {
        try
        {
            if (!sampler.ShouldEmit(snapshot.Sampling, status))
            {
                metrics.IncSampledOut();
                return;
            }

            byte[]? responseBody = tee?.CapturedBytes;
            var exchange = new CapturedExchange
            {
                EventId = Guid.CreateVersion7(),
                PartnerId = partnerId,
                Ts = ts,
                Method = context.Request.Method,
                Path = context.Request.Path.HasValue ? context.Request.Path.Value! : "/",
                Status = status,
                LatencyMs = (int)Math.Min(latencyMs, int.MaxValue),
                RequestHeaders = context.Request.Headers,
                ResponseHeaders = context.Response.Headers,
                RequestBody = requestBody,
                ResponseBody = responseBody is { Length: > 0 } ? responseBody : null,
                RequestContentType = context.Request.ContentType,
                ResponseContentType = context.Response.ContentType,
                RequestSize = (int)Math.Min(context.Request.ContentLength ?? requestBody?.Length ?? 0, int.MaxValue),
                // Fall back to the Content-Length header when body capture is off (tee is
                // null) so metadata-only mode still reports a known response size.
                ResponseSize = (int)Math.Min(tee?.TotalBytes ?? context.Response.ContentLength ?? 0, int.MaxValue),
            };

            IngestEventEnvelope envelope = ExchangeRedactor.Redact(exchange, snapshot, keys, metrics);
            channel.TryWrite(envelope);
            metrics.IncCaptured();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            metrics.IncRedactionFailure();
            FaultReporter.Report(options, new ConformanceFault(ConformanceFaultKind.Redaction, ex.Message, ex));
        }
    }
}
