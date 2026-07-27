# ToSpec.Sdk

**Production conformance for ASP.NET Core APIs — one line of middleware.**
`app.UseToSpecConformance()` redacts your API's request/response traffic *inside your
process* and streams it to ToSpec for drift detection and continuous certification. Sensitive
values never leave your infrastructure; the SDK never waits for ToSpec network I/O on a
request, never grows without bound, and never throws into your pipeline.

[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

This is the .NET SDK for the [ToSpec](https://tospec.dev) partner-certification platform. It
is open source because it runs in *your* production process — security review demands source,
and you should be able to read exactly what it does with your traffic. The redaction it
applies is the same engine the ToSpec gateway runs, published separately as
[`ToSpec.Redact`](https://github.com/ToSpec-Dev/redact-dotnet).

## The guarantees (they are the product)

- **Redaction before transmission.** Bodies are redacted with your compiled ruleset *before*
  a single byte is queued to send. Formats without a structured redactor, malformed bodies,
  and traffic seen before a ruleset loads are **dropped, never sent raw**. Proven by a
  structural test that scans the actual outbound wire bytes for planted secrets.
- **Never waits for ToSpec I/O on the request thread.** Capture is a bounded memory copy plus a fast
  synchronous redaction; the network send happens on a background worker. A hung or failing
  ingest endpoint cannot touch your request latency (measured: p99 unaffected with a dead
  ingest endpoint).
- **Bounded memory.** A count-and-byte-bounded queue drops the *oldest* event under
  backpressure and counts every drop. No unbounded buffering, ever.
- **Zero user-visible failures.** Every fault — a timeout, a bad response, a redaction error —
  is swallowed to a counter and an optional logging hook. The SDK cannot break your API.
- **Instant off switch.** Capture can be disabled remotely per tenant, and the SDK honors
  the kill switch within one config-poll interval — no deploy, no restart. Ask ToSpec to
  flip it; there is no self-serve control for this yet.

## Install

```sh
dotnet add package ToSpec.Sdk
```

## Quick start

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddToSpecConformance(options =>
{
    options.IngestBaseUrl = new Uri("https://ingest.tospec.net");
    options.IngestKey     = builder.Configuration["ToSpec:IngestKey"];   // tsp_ing_…
    options.RedactionKey  = Convert.FromHexString(builder.Configuration["ToSpec:RedactionKeyHex"]!);
    options.RedactionKeyVersion = 1;

    // Which partner is calling your API — map from your auth (API key, mTLS, JWT claim…).
    options.ResolvePartnerId = ctx =>
        ctx.Request.Headers.TryGetValue("X-Partner-Id", out var id) && Guid.TryParse(id, out var g)
            ? g : null;

    options.OnFault = fault => app.Logger.LogWarning("ToSpec {Kind}: {Message}", fault.Kind, fault.Message);
});

var app = builder.Build();

app.UseToSpecConformance();   // place early — it observes the whole request/response
// ... your endpoints ...
app.Run();
```

Your ingest key, redaction key, and ruleset are issued by ToSpec and provisioned against
your API — ask, rather than looking for a screen in the portal. The SDK
polls `GET /v1/sdk/config` for the ruleset, sampling, and kill switch, and POSTs gzip-signed
batches to `POST /v1/ingest`. See [`ToSpec-Dev/sdk-protocol`](https://github.com/ToSpec-Dev/sdk-protocol)
for the full wire contract.

## How it works

```
request ─► [middleware] ── capture metadata + bounded body copies
                          └─ redact locally (ToSpec.Redact) ──► redacted envelope
                                                                     │  non-blocking, drop-oldest
                                                                     ▼
                                              bounded queue ──► [background sender] ── gzip + sign ──► ingest
             [background poller] ── GET /v1/sdk/config (ETag/304) ──► ruleset · sampling · kill switch
```

The request thread does capture + redaction (microseconds) and a non-blocking enqueue —
nothing else. Everything with a network dependency runs in the background, decoupled by the
bounded queue.

## Configuration

| Option | Default | Notes |
|---|---|---|
| `IngestBaseUrl`, `IngestKey`, `RedactionKey`, `ResolvePartnerId` | — | required |
| `RedactionKeyVersion` | `1` | embedded in every `tsr_v{n}_…` token |
| `CaptureRequestBodies` / `CaptureResponseBodies` | `true` | |
| `MaxBodyBytes` | 64 KiB | per-body capture cap |
| `QueueCapacity` | 10,000 | the hard memory bound (drop-oldest above it) |
| `MaxQueueBytes` | 64 MiB | independent estimated queued-byte bound |
| `MaxBatchEvents` / `MaxBatchBytes` | 200 / 4 MiB | batch flush thresholds |
| `FlushInterval` | 5 s | max linger before flushing a partial batch |
| `ConfigPollInterval` | 15 s | kill switch takes effect within one interval |
| `IngestTimeout` | 10 s | bounds background sends; never affects requests |
| `OnFault` | — | logging hook for swallowed faults |

Resolve `ConformanceMetrics` from DI to export the SDK's counters (captured, dropped,
batches sent/failed, redaction failures, config polls, kill-switch state).

## Performance

The only work the SDK adds to the request thread is capture + redaction + a non-blocking
enqueue. Measured with the `benchmarks/` harness (BenchmarkDotNet):

`BenchmarkDotNet v0.15.4 · macOS 26.5 · Apple M3 Max · .NET SDK 10.0.100 · ShortRun`

| Scenario | Mean | Allocated |
|---|---:|---:|
| Metadata only (headers, no body) | ~0.52 µs | 1.19 KB |
| With a small JSON body (redacted) | ~1.45 µs | 2.54 KB |

Sub-microsecond for metadata; a small JSON body redaction is ~1.4 µs — negligible against
any real handler. Reproduce with:

```sh
dotnet run -c Release --project benchmarks/ToSpec.Sdk.Benchmarks -- --filter '*'
```

## Building from source

```sh
dotnet build -warnaserror
dotnet test
```

Requires the .NET 10 SDK (pinned in `global.json`). The one runtime dependency is
`ToSpec.Redact`; everything else is the BCL and the ASP.NET Core shared framework.

## License

[Apache-2.0](LICENSE).
