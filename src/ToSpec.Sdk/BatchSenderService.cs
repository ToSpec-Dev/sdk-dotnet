using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using ToSpec.Sdk.Wire;

namespace ToSpec.Sdk;

/// <summary>
/// The background batcher (SPEC-cert-gateway-architecture §9): drains the bounded queue,
/// accumulates a batch up to the size/count/linger caps, then gzips, signs, and POSTs it
/// to the ingest edge. It is the only place the SDK touches the network, and it runs off
/// the request threads — so a hung or failing ingest endpoint can never affect host
/// request latency. <b>Every</b> fault (timeout, DNS, 4xx/5xx, serialization) is swallowed
/// to counters and the logging hook; a rejected batch is dropped, never retried unbounded
/// (which would defeat the memory bound).
/// </summary>
internal sealed class BatchSenderService : BackgroundService
{
    private readonly ToSpecConformanceOptions _options;
    private readonly ConformanceChannel _channel;
    private readonly ConformanceMetrics _metrics;
    private readonly ConformanceState _state;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Uri _ingestUri;
    private readonly byte[] _keyUtf8;
    private readonly string _ingestKey;

    public BatchSenderService(
        ToSpecConformanceOptions options,
        ConformanceChannel channel,
        ConformanceMetrics metrics,
        ConformanceState state,
        IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _channel = channel;
        _metrics = metrics;
        _state = state;
        _httpClientFactory = httpClientFactory;
        _ingestUri = new Uri(options.IngestBaseUrl!, ToSpecConformance.IngestPath);
        _ingestKey = options.IngestKey!;
        _keyUtf8 = Encoding.UTF8.GetBytes(_ingestKey);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<IngestEventEnvelope>(_options.MaxBatchEvents);
        while (!stoppingToken.IsCancellationRequested)
        {
            IngestEventEnvelope first;
            try
            {
                first = await _channel.ReadAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            batch.Add(first);
            await AccumulateAsync(batch, stoppingToken);
            if (stoppingToken.IsCancellationRequested)
            {
                break; // retain the current batch for the fresh shutdown deadline below
            }
            if (_state.Current.KillSwitch)
            {
                batch.Clear();
                while (_channel.TryRead(out _))
                {
                    // Drop queued pre-switch events. A later config poll that clears
                    // the switch resumes only with events captured after that point.
                }
                continue;
            }
            await SendAsync(batch, stoppingToken);
            batch.Clear();
        }

        // Best-effort final flush of whatever is still queued at shutdown, bounded so a dead
        // ingest endpoint cannot stall host shutdown.
        using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (_channel.TryRead(out IngestEventEnvelope leftover))
        {
            batch.Add(leftover);
            if (batch.Count >= _options.MaxBatchEvents)
            {
                await SendAsync(batch, shutdownCts.Token);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await SendAsync(batch, shutdownCts.Token);
        }
    }

    /// <summary>Fills the batch up to the count/byte caps, lingering at most
    /// <see cref="ToSpecConformanceOptions.FlushInterval"/> for more events.</summary>
    private async Task AccumulateAsync(List<IngestEventEnvelope> batch, CancellationToken stoppingToken)
    {
        long bytes = EstimateBytes(batch[0]);
        long deadlineTicks = Environment.TickCount64 + (long)_options.FlushInterval.TotalMilliseconds;

        while (batch.Count < _options.MaxBatchEvents && bytes < _options.MaxBatchBytes)
        {
            long remaining = deadlineTicks - Environment.TickCount64;
            if (remaining <= 0)
            {
                return;
            }

            using var linger = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            linger.CancelAfter(TimeSpan.FromMilliseconds(remaining));
            try
            {
                IngestEventEnvelope next = await _channel.ReadAsync(linger.Token);
                batch.Add(next);
                bytes += EstimateBytes(next);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Linger deadline hit — flush the partial batch.
                return;
            }
            catch (OperationCanceledException)
            {
                // Host is shutting down — flush what we have.
                return;
            }
        }
    }

    private async Task SendAsync(List<IngestEventEnvelope> events, CancellationToken stoppingToken)
    {
        if (events.Count == 0 || _state.Current.KillSwitch)
        {
            return;
        }

        int count = events.Count;
        try
        {
            var batch = new IngestBatch
            {
                BatchId = Guid.CreateVersion7(),
                Events = events.ToArray(),
            };

            byte[] json = JsonSerializer.SerializeToUtf8Bytes(batch, SdkJsonContext.Default.IngestBatch);
            byte[] wire = Gzip(json);
            string signature = IngestSigner.Sign(_keyUtf8, wire);

            HttpClient client = _httpClientFactory.CreateClient(ToSpecConformance.HttpClientName);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                using var content = new ByteArrayContent(wire);
                content.Headers.ContentEncoding.Add("gzip");
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                using var request = new HttpRequestMessage(HttpMethod.Post, _ingestUri) { Content = content };
                request.Headers.TryAddWithoutValidation(IngestSigner.IngestKeyHeader, _ingestKey);
                request.Headers.TryAddWithoutValidation(IngestSigner.SignatureHeader, signature);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(_options.IngestTimeout);
                using HttpResponseMessage response = await client.SendAsync(request, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    _metrics.IncBatchSent(count);
                    return;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Conflict && attempt < 2)
                {
                    await Task.Delay(attempt == 0 ? 100 : 250, stoppingToken);
                    continue;
                }

                _metrics.IncBatchFailed();
                FaultReporter.Report(_options, new ConformanceFault(
                    ConformanceFaultKind.BatchSend,
                    $"ingest returned {(int)response.StatusCode}",
                    null));
                return;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down — drop this batch quietly.
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            _metrics.IncBatchFailed();
            FaultReporter.Report(_options, new ConformanceFault(ConformanceFaultKind.BatchSend, ex.Message, ex));
        }
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>A cheap upper-ish estimate of an event's JSON footprint, to cap batch size
    /// without serializing twice. Bodies dominate; base64 is ~4/3 of the raw bytes.</summary>
    private static long EstimateBytes(IngestEventEnvelope e) =>
        256 + (e.ReqBody?.Length ?? 0) + (e.RespBody?.Length ?? 0);
}
