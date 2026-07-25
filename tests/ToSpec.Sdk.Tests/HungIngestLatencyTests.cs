using System.Diagnostics;
using System.Net.Http;
using ToSpec.Sdk.Tests.Support;

namespace ToSpec.Sdk.Tests;

/// <summary>
/// The "never blocks the request thread" guarantee, measured. The ingest endpoint hangs
/// forever; because the SDK sends off the request thread (decoupled by the bounded queue),
/// host request latency is unaffected. If capture were on the hot path, a 30s hung ingest
/// would show up in the p99.
/// </summary>
public sealed class HungIngestLatencyTests
{
    [Fact]
    public async Task HungIngestEndpoint_LeavesHostRequestLatencyUnaffected()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        var config = new FakeConfig { RulesetVersion = 0, CompiledJson = null };
        var handler = new RecordingHandler(
            Responders.Standard(config, (_, c) => Responders.HangAsync(c)));

        await using var harness = await ConformanceHarness.StartAsync(
            options =>
            {
                // Long enough that any coupling to a hung POST would be obvious.
                options.IngestTimeout = TimeSpan.FromSeconds(30);
                options.QueueCapacity = 20_000;
            },
            handler,
            cancellationToken: ct);

        // Warm up the pipeline/JIT so the measurement reflects steady state.
        for (int i = 0; i < 10; i++)
        {
            using HttpResponseMessage warm = await harness.Client.GetAsync("/warmup", ct);
            warm.EnsureSuccessStatusCode();
        }

        var latencies = new List<double>(200);
        for (int i = 0; i < 200; i++)
        {
            long start = Stopwatch.GetTimestamp();
            using HttpResponseMessage response = await harness.Client.GetAsync("/x", ct);
            response.EnsureSuccessStatusCode();
            latencies.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        latencies.Sort();
        double p99 = latencies[(int)(latencies.Count * 0.99)];

        // In-process requests are single-digit ms; the bound is generous to avoid CI
        // flakiness but is orders of magnitude below the 30s hung-ingest timeout.
        Assert.True(p99 < 1000, $"p99 host request latency was {p99:F1}ms while ingest was hung");

        // Prove the sender genuinely attempted to POST (and is now stuck) — the isolation is
        // real, not an artifact of the SDK never trying.
        await harness.WaitUntilAsync(
            () => handler.IngestPostCount > 0,
            TimeSpan.FromSeconds(5),
            "the background sender to attempt a POST against the hung endpoint");
    }
}
