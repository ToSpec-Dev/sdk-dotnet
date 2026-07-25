using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Http;
using ToSpec.Sdk.Tests.Support;

namespace ToSpec.Sdk.Tests;

public sealed class RequestPathIsolationTests
{
    [Fact]
    public async Task RewoundRequestBody_IsCapturedOnceWithoutDuplicatedPrefix()
    {
        byte[] body = "{\"value\":42}"u8.ToArray();
        await using var inner = new MemoryStream(body);
        await using var tee = new ReadTeeStream(inner, 1024);
        var prefix = new byte[5];
        _ = await tee.ReadAsync(prefix, TestContext.Current.CancellationToken);
        tee.Position = 0;
        using var reader = new MemoryStream();
        await tee.CopyToAsync(reader, TestContext.Current.CancellationToken);

        Assert.Equal(body, tee.CapturedBytes);
    }

    [Fact]
    public async Task UnreadSlowRequestBody_DoesNotDelayTheApplication()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(Responders.Standard(new FakeConfig()));
        var releaseBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        static Task App(HttpContext context)
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask; // Deliberately never reads the streaming upload.
        }

        await using var harness = await ConformanceHarness.StartAsync(
            _ => { }, handler, App, cancellationToken: ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/stream")
        {
            Content = new BlockingContent(releaseBody.Task),
        };

        Task<HttpResponseMessage> pending = harness.Client.SendAsync(request, ct);
        Task winner = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(1), ct));
        Assert.Same(pending, winner);
        using HttpResponseMessage response = await pending;
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        releaseBody.TrySetResult();
    }

    [Fact]
    public async Task ThrowingFaultHook_NeverEscapesTheMiddleware()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(Responders.Standard(new FakeConfig()));
        await using var harness = await ConformanceHarness.StartAsync(
            options =>
            {
                options.ResolvePartnerId = _ => throw new InvalidOperationException("resolver broke");
                options.OnFault = _ => throw new InvalidOperationException("logger broke");
            },
            handler,
            cancellationToken: ct);

        using HttpResponseMessage response = await harness.Client.GetAsync("/still-healthy", ct);
        response.EnsureSuccessStatusCode();
    }

    private sealed class BlockingContent(Task release) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(
            Stream stream, TransportContext? context)
        {
            await release;
            await stream.WriteAsync(new byte[1024]);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 1024;
            return true;
        }
    }
}
