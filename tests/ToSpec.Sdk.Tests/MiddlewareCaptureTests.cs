using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ToSpec.Sdk.Tests.Support;

namespace ToSpec.Sdk.Tests;

/// <summary>
/// Request-path capture correctness: a thrown endpoint must be recorded as a failure (not a
/// default-200 success), and metadata-only mode must still report a known response size.
/// </summary>
public sealed class MiddlewareCaptureTests
{
    private const string DropRuleset =
        """
        body:
          - { path: "$.x", action: drop }
        """;

    private static JsonElement FirstEvent(CapturedRequest post)
    {
        using var doc = JsonDocument.Parse(post.BodyText());
        return doc.RootElement.GetProperty("events")[0].Clone();
    }

    [Fact]
    public async Task ThrownEndpoint_WithErrorOnlySampling_EmittedWithStatus500()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // errors:100, success:0 — a request misread as a success (default 200) would be
        // sampled out entirely. A correctly-classified failure is kept.
        var config = new FakeConfig
        {
            RulesetVersion = 1,
            CompiledJson = Rulesets.CompiledJson(DropRuleset),
            SamplingJson = "{\"errors\":100,\"success\":0}",
        };
        var handler = new RecordingHandler(Responders.Standard(config));

        static Task Throws(HttpContext _) => throw new InvalidOperationException("boom");

        await using var harness = await ConformanceHarness.StartAsync(_ => { }, handler, Throws, ct);
        await harness.WaitUntilAsync(
            () => harness.State.Current.Ruleset is not null, TimeSpan.FromSeconds(5), "config to load");

        try
        {
            using HttpResponseMessage _ = await harness.Client.GetAsync("/boom", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // TestServer surfaces the unhandled exception; capture already ran in the
            // middleware's finally before it propagated here.
        }

        await harness.WaitUntilAsync(
            () => handler.IngestPostCount > 0, TimeSpan.FromSeconds(5),
            "the failed request to be emitted (not sampled out as a success)");

        JsonElement ev = FirstEvent(handler.Requests.First(r => r.IsIngestPost));
        Assert.Equal(500, ev.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task ResponseSize_FromContentLength_WhenBodyCaptureDisabled()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        var config = new FakeConfig { RulesetVersion = 1, CompiledJson = Rulesets.CompiledJson(DropRuleset) };
        var handler = new RecordingHandler(Responders.Standard(config));

        const string Payload = """{"ok":true}"""; // 11 bytes
        static async Task App(HttpContext context)
        {
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = Payload.Length;
            await context.Response.WriteAsync(Payload, context.RequestAborted);
        }

        await using var harness = await ConformanceHarness.StartAsync(
            options => options.CaptureResponseBodies = false, handler, App, ct);
        await harness.WaitUntilAsync(
            () => harness.State.Current.Ruleset is not null, TimeSpan.FromSeconds(5), "config to load");

        using HttpResponseMessage response = await harness.Client.GetAsync("/x", ct);
        response.EnsureSuccessStatusCode();

        await harness.WaitUntilAsync(
            () => handler.IngestPostCount > 0, TimeSpan.FromSeconds(5), "the SDK to POST a batch");

        JsonElement ev = FirstEvent(handler.Requests.First(r => r.IsIngestPost));
        Assert.Equal(Payload.Length, ev.GetProperty("resp_size").GetInt32());
    }
}
