using System.Net;
using System.Net.Http;
using ToSpec.Sdk.Tests.Support;

namespace ToSpec.Sdk.Tests;

public sealed class SenderReliabilityTests
{
    [Fact]
    public async Task Conflict_IsRetriedWithIdenticalBatchBytes()
    {
        int attempts = 0;
        var handler = new RecordingHandler(Responders.Standard(
            new FakeConfig(),
            (_, _) => Task.FromResult(++attempts < 3
                ? new HttpResponseMessage(HttpStatusCode.Conflict)
                : Responders.Accepted())));
        await using var harness = await ConformanceHarness.StartAsync(_ => { }, handler,
            cancellationToken: TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await harness.Client.GetAsync(
            "/retry", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        await harness.WaitUntilAsync(
            () => handler.IngestPostCount == 3,
            TimeSpan.FromSeconds(5),
            "the retryable 409 batch to succeed");

        byte[][] bodies = [.. handler.Requests.Where(r => r.IsIngestPost).Select(r => r.Body!)];
        Assert.Equal(bodies[0], bodies[1]);
        Assert.Equal(bodies[0], bodies[2]);
    }

    [Fact]
    public async Task HostShutdown_FlushesTheAlreadyAccumulatedBatch()
    {
        var handler = new RecordingHandler(Responders.Standard(new FakeConfig()));
        var harness = await ConformanceHarness.StartAsync(
            options => options.FlushInterval = TimeSpan.FromSeconds(30),
            handler,
            cancellationToken: TestContext.Current.CancellationToken);
        using HttpResponseMessage response = await harness.Client.GetAsync(
            "/shutdown", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        await harness.DisposeAsync();

        Assert.Equal(1, handler.IngestPostCount);
    }
}
