using System.Net.Http;
using ToSpec.Sdk.Tests.Support;
using ToSpec.Sdk.Wire;

namespace ToSpec.Sdk.Tests;

/// <summary>
/// The "bounded memory" guarantee: under backpressure the queue never grows past its
/// capacity — it drops the oldest event and counts every drop. Proven twice: at the
/// channel primitive (exact drop accounting) and end-to-end through the middleware with a
/// stalled sender (requests still succeed; drops accrue).
/// </summary>
public sealed class BoundedMemoryFloodTests
{
    [Fact]
    public void OversizedEvent_IsDroppedBeforeEvictingQueuedEvents()
    {
        var metrics = new ConformanceMetrics();
        var channel = new ConformanceChannel(10, 2_000, metrics);
        static IngestEventEnvelope Small(string path) => new()
        {
            EventId = Guid.CreateVersion7(), PartnerId = Guid.CreateVersion7(),
            Ts = DateTimeOffset.UtcNow, Direction = "inbound", Method = "GET", Path = path,
        };
        channel.TryWrite(Small("/one"));
        channel.TryWrite(Small("/two"));
        channel.TryWrite(Small(new string('x', 3_000)));

        Assert.True(channel.TryRead(out IngestEventEnvelope first));
        Assert.True(channel.TryRead(out IngestEventEnvelope second));
        Assert.Equal("/one", first.Path);
        Assert.Equal("/two", second.Path);
        Assert.False(channel.TryRead(out _));
        Assert.Equal(1, metrics.Snapshot().EventsDroppedQueueFull);
    }
    [Fact]
    public void BoundedChannel_DropsOldest_AndCountsEveryDrop()
    {
        var metrics = new ConformanceMetrics();
        const int capacity = 100;
        const int flood = 1000;
        var channel = new ConformanceChannel(capacity, 64 * 1024 * 1024, metrics);

        for (int i = 0; i < flood; i++)
        {
            // Drop-oldest means the write always succeeds (evicting the oldest if full).
            Assert.True(channel.TryWrite(new IngestEventEnvelope { EventId = Guid.CreateVersion7() }));
        }

        ConformanceMetricsSnapshot snapshot = metrics.Snapshot();
        Assert.Equal(flood - capacity, snapshot.EventsDroppedQueueFull);

        // The channel holds exactly capacity — it never grew unbounded.
        int drained = 0;
        while (channel.TryRead(out _))
        {
            drained++;
        }

        Assert.Equal(capacity, drained);
    }

    [Fact]
    public void ByteBudget_DropsOldest_EvenBelowEventCapacity()
    {
        var metrics = new ConformanceMetrics();
        var channel = new ConformanceChannel(100, 2_000, metrics);
        for (int i = 0; i < 10; i++)
        {
            Assert.True(channel.TryWrite(new IngestEventEnvelope
            {
                EventId = Guid.CreateVersion7(),
                ReqBody = Convert.ToBase64String(new byte[600]),
            }));
        }

        Assert.True(metrics.Snapshot().EventsDroppedQueueFull > 0);
        int retained = 0;
        while (channel.TryRead(out _))
        {
            retained++;
        }
        Assert.InRange(retained, 1, 2);
    }

    [Fact]
    public async Task UnderFlood_WithStalledSender_RequestsStillSucceed_AndOldestDropped()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        var config = new FakeConfig { RulesetVersion = 0, CompiledJson = null };
        var handler = new RecordingHandler(
            Responders.Standard(config, (_, c) => Responders.HangAsync(c)));

        await using var harness = await ConformanceHarness.StartAsync(
            options =>
            {
                options.QueueCapacity = 50;
                options.MaxBatchEvents = 5;
            },
            handler,
            cancellationToken: ct);

        for (int i = 0; i < 500; i++)
        {
            using HttpResponseMessage response = await harness.Client.GetAsync("/x", ct);
            response.EnsureSuccessStatusCode();
        }

        await harness.WaitUntilAsync(
            () => harness.Metrics.Snapshot().EventsDroppedQueueFull > 0,
            TimeSpan.FromSeconds(5),
            "drops to accrue while the sender is stalled");

        ConformanceMetricsSnapshot snapshot = harness.Metrics.Snapshot();
        Assert.True(snapshot.EventsCaptured >= 500, $"expected ≥500 captured, got {snapshot.EventsCaptured}");
        Assert.True(snapshot.EventsDroppedQueueFull > 0, "expected oldest events to be dropped under flood");
    }
}
