using System.Net.Http;
using Microsoft.AspNetCore.Http;
using ToSpec.Sdk.Tests.Support;

namespace ToSpec.Sdk.Tests;

/// <summary>
/// The SDK-level structural guarantee: sensitive values planted into a request/response
/// never survive onto the wire. This is the SDK twin of ToSpec.Redact's
/// <c>RedactionGuaranteeTests</c> and the platform gateway's raw-column-scan test — but at
/// the point that actually matters for a client-side SDK: the <b>outbound HTTP bytes</b>.
/// A Luhn PAN in a body, a PAN echoed in the response, and an auth-shaped secret in a
/// header all go in; the actual (gunzipped) batch the SDK POSTs is scanned and the raw
/// bytes are absent, replaced by deterministic tokens.
/// </summary>
public sealed class RedactionBeforeTransmissionTests
{
    private const string Pan = "4111111111111111";
    private const string BearerSecret = "tsk-secret-sentinel-9d1f7c2ab4";

    private const string RulesetYaml =
        """
        body:
          - { path: "$.card", action: hash }
          - { path: "$..password", action: drop }
        freetext:
          scan_unknown: true
          detectors: [pan_luhn, email]
        defaults:
          unknown_pii_policy: detect_and_hash
        """;

    [Fact]
    public async Task PanAndAuthSecret_PlantedIntoExchange_AbsentFromOutboundWireBytes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        var config = new FakeConfig
        {
            RulesetVersion = 3,
            CompiledJson = Rulesets.CompiledJson(RulesetYaml),
        };
        var handler = new RecordingHandler(Responders.Standard(config));

        // The provider's endpoint echoes the PAN back in its response body — the SDK must
        // redact the response body too, not just the request.
        static async Task Echo(HttpContext context)
        {
            using var reader = new StreamReader(context.Request.Body);
            _ = await reader.ReadToEndAsync(context.RequestAborted);
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync($$"""{"card":"{{Pan}}","note":"charge {{Pan}} now"}""", context.RequestAborted);
        }

        await using var harness = await ConformanceHarness.StartAsync(_ => { }, handler, Echo, ct);

        await harness.WaitUntilAsync(
            () => harness.State.Current.Ruleset is not null,
            TimeSpan.FromSeconds(5),
            "config poll to load the ruleset");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = new StringContent(
                $$"""{"card":"{{Pan}}","password":"hunter2"}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {BearerSecret}");
        using HttpResponseMessage response = await harness.Client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        await harness.WaitUntilAsync(
            () => handler.IngestPostCount > 0,
            TimeSpan.FromSeconds(5),
            "the SDK to POST a batch");

        CapturedRequest post = handler.Requests.First(r => r.IsIngestPost);
        // Scan the batch INCLUDING base64-decoded bodies — a raw-wire grep would falsely
        // pass on a body-borne PAN because base64 hides the literal digits.
        string scannable = WireInspector.ScannableText(post);

        Assert.DoesNotContain(Pan, scannable, StringComparison.Ordinal);
        Assert.DoesNotContain(BearerSecret, scannable, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer ", scannable, StringComparison.Ordinal);
        // The card was hashed with key version 1 → a tsr_v1_ token stands in its place
        // (present inside a decoded body, proving redaction ran, not that the body was dropped).
        Assert.Contains("tsr_v1_", scannable, StringComparison.Ordinal);
        // Sanity: the batch really carried this exchange (stamped with the ruleset version).
        Assert.Contains("\"redaction_version\":3", post.BodyText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BeforeConfigLoads_BodyIsDropped_NeverTransmittedRaw()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Config never returns a ruleset (version 0, no compiled). The fail-safe: bodies are
        // dropped entirely rather than transmitted unredacted.
        var config = new FakeConfig { RulesetVersion = 0, CompiledJson = null };
        var handler = new RecordingHandler(Responders.Standard(config));

        await using var harness = await ConformanceHarness.StartAsync(_ => { }, handler, cancellationToken: ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = new StringContent(
                $$"""{"card":"{{Pan}}"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        using HttpResponseMessage response = await harness.Client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        await harness.WaitUntilAsync(
            () => handler.IngestPostCount > 0,
            TimeSpan.FromSeconds(5),
            "the SDK to POST a batch");

        CapturedRequest post = handler.Requests.First(r => r.IsIngestPost);
        Assert.DoesNotContain(Pan, WireInspector.ScannableText(post), StringComparison.Ordinal);
        // Body fields are omitted (dropped), so no req_body/resp_body carrying content.
        Assert.DoesNotContain("\"req_body\"", post.BodyText(), StringComparison.Ordinal);
    }
}
