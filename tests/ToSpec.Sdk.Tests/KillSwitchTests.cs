using System.Net.Http;
using ToSpec.Sdk.Tests.Support;

namespace ToSpec.Sdk.Tests;

/// <summary>The kill switch stops emission within one poll interval (the "turn it off
/// instantly" requirement, SPEC-cert-gateway-architecture §9). A flip lands as a changed
/// config; the very next captured request is passed through untouched.</summary>
public sealed class KillSwitchTests
{
    [Fact]
    public async Task KillSwitch_Flipped_StopsEmissionWithinOnePoll()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        var config = new FakeConfig { RulesetVersion = 0, KillSwitch = false };
        var handler = new RecordingHandler(Responders.Standard(config));

        await using var harness = await ConformanceHarness.StartAsync(_ => { }, handler, cancellationToken: ct);

        await harness.WaitUntilAsync(
            () => harness.Metrics.Snapshot().ConfigPolls > 0,
            TimeSpan.FromSeconds(5),
            "the first config poll");

        // Emit one event and confirm it reaches the ingest edge.
        using (HttpResponseMessage first = await harness.Client.GetAsync("/before", ct))
        {
            first.EnsureSuccessStatusCode();
        }

        await harness.WaitUntilAsync(
            () => handler.IngestPostCount > 0,
            TimeSpan.FromSeconds(5),
            "the first batch to be POSTed");

        // Flip the kill switch and wait for it to propagate.
        config.KillSwitch = true;
        config.Bump();
        await harness.WaitUntilAsync(
            () => harness.State.Current.KillSwitch,
            TimeSpan.FromSeconds(5),
            "the kill switch to reach the SDK");

        int postsBefore = handler.IngestPostCount;
        long capturedBefore = harness.Metrics.Snapshot().EventsCaptured;

        for (int i = 0; i < 10; i++)
        {
            using HttpResponseMessage response = await harness.Client.GetAsync("/after", ct);
            response.EnsureSuccessStatusCode();
        }

        // Give the sender more than a flush interval to (not) send anything new.
        await Task.Delay(400, ct);

        Assert.Equal(postsBefore, handler.IngestPostCount);
        Assert.Equal(capturedBefore, harness.Metrics.Snapshot().EventsCaptured);
        Assert.True(harness.Metrics.Snapshot().KillSwitchActive);
    }

    [Fact]
    public async Task KillSwitch_DropsEventsThatWereQueuedBeforeThePoll()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var config = new FakeConfig { RulesetVersion = 0, KillSwitch = false };
        var handler = new RecordingHandler(Responders.Standard(config));
        await using var harness = await ConformanceHarness.StartAsync(
            options =>
            {
                options.FlushInterval = TimeSpan.FromSeconds(2);
                options.MaxBatchEvents = 200;
            },
            handler,
            cancellationToken: ct);

        await harness.WaitUntilAsync(
            () => harness.Metrics.Snapshot().ConfigPolls > 0,
            TimeSpan.FromSeconds(5),
            "the first config poll");
        for (int i = 0; i < 25; i++)
        {
            using HttpResponseMessage response = await harness.Client.GetAsync($"/queued/{i}", ct);
            response.EnsureSuccessStatusCode();
        }

        config.KillSwitch = true;
        config.Bump();
        await harness.WaitUntilAsync(
            () => harness.State.Current.KillSwitch,
            TimeSpan.FromSeconds(5),
            "the kill switch poll");

        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        Assert.Equal(0, handler.IngestPostCount);
    }
}
