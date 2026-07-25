using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Http;
using ToSpec.Redact;
using ToSpec.Sdk;

namespace ToSpec.Sdk.Benchmarks;

/// <summary>
/// Measures the per-request CPU the middleware adds on the hot path: header strip/hash plus
/// body redaction plus envelope construction (the work done synchronously before the
/// non-blocking enqueue). Network I/O is off the request thread and not measured here. The
/// point is to show this cost is small — the "never blocks the request thread / host p99
/// unchanged" guarantee is about latency isolation, and this is the only work that touches
/// the request path.
/// </summary>
[MemoryDiagnoser]
public class ExchangeRedactorBenchmarks
{
    private readonly ConformanceMetrics _metrics = new();
    private readonly RedactionKeyring _keys = new(new byte[32], 1);
    private ConformanceSnapshot _snapshot = ConformanceSnapshot.Initial;
    private CapturedExchange _metadataOnly = null!;
    private CapturedExchange _withJsonBody = null!;

    [GlobalSetup]
    public void Setup()
    {
        CompiledRuleset ruleset = RedactionRulesetCompiler.Compile(
            "body:\n  - { path: \"$.payment.cardNumber\", action: hash }\nheaders:\n  hash: [X-Client-Id]")
            .Ruleset!;
        _snapshot = new ConformanceSnapshot { Ruleset = ruleset, RulesetVersion = 3 };

        var reqHeaders = new HeaderDictionary
        {
            ["Authorization"] = "Bearer secret-token-value",
            ["X-Client-Id"] = "client-4711",
            ["Accept"] = "application/json",
            ["User-Agent"] = "acme-partner/2.1",
        };
        var respHeaders = new HeaderDictionary { ["Content-Type"] = "application/json" };
        byte[] body = Encoding.UTF8.GetBytes(
            """{"payment":{"cardNumber":"4111111111111111","amount":"149.00"},"guest":{"name":"Ada"}}""");

        _metadataOnly = new CapturedExchange
        {
            EventId = Guid.Empty,
            PartnerId = Guid.Empty,
            Ts = DateTimeOffset.UnixEpoch,
            Method = "GET",
            Path = "/v1/reservations",
            Status = 200,
            LatencyMs = 12,
            RequestHeaders = reqHeaders,
            ResponseHeaders = respHeaders,
        };

        _withJsonBody = new CapturedExchange
        {
            EventId = Guid.Empty,
            PartnerId = Guid.Empty,
            Ts = DateTimeOffset.UnixEpoch,
            Method = "POST",
            Path = "/v1/payments",
            Status = 201,
            LatencyMs = 20,
            RequestHeaders = reqHeaders,
            ResponseHeaders = respHeaders,
            RequestBody = body,
            RequestContentType = "application/json",
            RequestSize = body.Length,
        };
    }

    [Benchmark(Baseline = true)]
    public object MetadataOnly() => ExchangeRedactor.Redact(_metadataOnly, _snapshot, _keys, _metrics);

    [Benchmark]
    public object WithJsonBody() => ExchangeRedactor.Redact(_withJsonBody, _snapshot, _keys, _metrics);
}
