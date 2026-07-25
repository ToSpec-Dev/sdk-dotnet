using ToSpec.Sdk.Wire;

namespace ToSpec.Sdk;

/// <summary>A non-blocking drop-oldest queue bounded by both event count and an
/// estimated byte budget. Producers never wait; the single reader is signalled
/// when an empty queue receives work.</summary>
internal sealed class ConformanceChannel
{
    private readonly object _gate = new();
    private readonly Queue<(IngestEventEnvelope Event, long Bytes)> _queue = new();
    private readonly int _capacity;
    private readonly long _maxBytes;
    private readonly ConformanceMetrics _metrics;
    private long _bytes;
    private TaskCompletionSource _available = NewSignal();

    public ConformanceChannel(int capacity, long maxBytes, ConformanceMetrics metrics)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        _capacity = capacity;
        _maxBytes = maxBytes;
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    public bool TryWrite(IngestEventEnvelope envelope)
    {
        long bytes = EstimateBytes(envelope);
        lock (_gate)
        {
            if (bytes > _maxBytes)
            {
                _metrics.IncDroppedQueueFull();
                return true;
            }

            while (_queue.Count > 0 && (_queue.Count >= _capacity || _bytes + bytes > _maxBytes))
            {
                (_, long droppedBytes) = _queue.Dequeue();
                _bytes -= droppedBytes;
                _metrics.IncDroppedQueueFull();
            }

            bool wasEmpty = _queue.Count == 0;
            _queue.Enqueue((envelope, bytes));
            _bytes += bytes;
            if (wasEmpty)
            {
                _available.TrySetResult();
            }
            return true;
        }
    }

    public async ValueTask<IngestEventEnvelope> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (TryReadLocked(out IngestEventEnvelope envelope))
                {
                    return envelope;
                }
                wait = _available.Task;
            }
            await wait.WaitAsync(cancellationToken);
        }
    }

    public bool TryRead(out IngestEventEnvelope envelope)
    {
        lock (_gate)
        {
            return TryReadLocked(out envelope);
        }
    }

    private bool TryReadLocked(out IngestEventEnvelope envelope)
    {
        if (_queue.Count == 0)
        {
            envelope = null!;
            return false;
        }

        (envelope, long bytes) = _queue.Dequeue();
        _bytes -= bytes;
        if (_queue.Count == 0)
        {
            _available = NewSignal();
        }
        return true;
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static long EstimateBytes(IngestEventEnvelope e) =>
        512L + (e.ReqBody?.Length ?? 0) + (e.RespBody?.Length ?? 0)
        + e.Method.Length + e.Path.Length
        + HeaderBytes(e.ReqHeaders) + HeaderBytes(e.RespHeaders);

    private static long HeaderBytes(IReadOnlyDictionary<string, string>? headers) =>
        headers?.Sum(pair => (long)pair.Key.Length + pair.Value.Length + 4) ?? 0;
}
