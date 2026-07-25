# CLAUDE.md — repo conventions for ToSpec.Sdk

`ToSpec.Sdk` is the **open-source .NET production conformance SDK** for the ToSpec
certification platform: ASP.NET Core middleware (`app.UseToSpecConformance()`) that redacts a
provider's API traffic *inside their process* and ships gzip-signed batches to the ToSpec
ingest edge. It consumes the public `ToSpec.Redact` engine as a package (the same redaction
the gateway runs) and speaks the wire protocol pinned by `ToSpec-Dev/sdk-protocol`. This repo is
the public, Apache-2.0 home of that SDK and the reference implementation other language ports
follow.

## The guarantees are the product (SPEC-cert-gateway-architecture §9)

These are not features to be traded off — they are the contract. Any change must keep all of
them, and each has a test that fails if it regresses:

- **Redaction before transmission.** Redaction runs synchronously on the request path and the
  redacted envelope is what gets enqueued — raw bytes never enter the shared queue. No
  ruleset yet, an unstructured format, or a malformed body ⇒ the body is **dropped**, never
  transmitted. Guarded by `RedactionBeforeTransmissionTests` (scans the actual outbound
  wire bytes, base64-decoding bodies).
- **Never block the request thread.** The request path does a bounded copy + fast redaction +
  a non-blocking `TryWrite`. All network I/O is on background hosted services, decoupled by
  the bounded channel. Guarded by `HungIngestLatencyTests` (measured).
- **Bounded memory.** `Channel.CreateBounded` with `FullMode = DropOldest` and the
  `itemDropped` counter. Guarded by `BoundedMemoryFloodTests`.
- **Zero user-visible failures.** Every fault is swallowed to `ConformanceMetrics` + the
  `OnFault` hook via a filtered `catch (Exception ex) when (ex is not OperationCanceledException)`.
  Never surface an exception into the host pipeline.
- **Kill switch within one poll.** `ConfigPollService` folds the kill switch into the snapshot
  and the middleware short-circuits on it. Guarded by `KillSwitchTests`.

If a change would weaken any of these, stop — the whole value proposition is that a provider
can run this in prod and trust it.

## Stack rules (non-negotiable)

- **.NET 10**, C# only. `net10.0`, pinned by `global.json`.
- **Dependency budget: one third-party runtime package — `ToSpec.Redact`.** Everything else
  is the BCL (`System.Threading.Channels`, `System.Text.Json`, `System.Security.Cryptography`,
  `System.IO.Compression`) and the ASP.NET Core shared framework (`FrameworkReference
  Microsoft.AspNetCore.App`, which includes `IHttpClientFactory`). No new NuGet dependency
  without justifying it in the PR. Do not reference platform (`ToSpec.*`) code — the SDK owns
  its copy of the wire DTOs; `sdk-protocol` keeps them in lockstep.
- **Warnings are errors** — repo-wide via `Directory.Build.props` (analyzers
  `latest-recommended`, `EnforceCodeStyleInBuild`). Fix the warning; suppress only with a
  comment and sign-off.
- **`ToSpec.Redact` is consumed, never reimplemented.** The SDK deserializes the compiled
  ruleset (`CompiledRulesetSerializer`), applies it (`BodyRedactorRegistry`), and tokenizes
  (`HmacTokenizer`) — it never re-derives redaction. Header strip/hash mirrors the gateway's
  `RedactedHeaderSerializer`.

## Wire contract stability

The ingest/config wire shapes and the signature recipe are a contract shared with the
platform and every SDK port. They are locked by the `ToSpec-Dev/sdk-protocol` golden fixtures
(`fixtures/`), regenerated from this repo (`FixtureFactory`). **Changing the envelope, the
signature, or the token/serialization format is a breaking change** — additive and
schema-versioned only, and the fixtures must be regenerated (run the tests with
`TOSPEC_WRITE_FIXTURES=<sdk-protocol>/fixtures;<here>/tests/ToSpec.Sdk.Tests/fixtures`) and
committed to both this repo and `sdk-protocol`.

## Repo layout

```
src/ToSpec.Sdk/                    the SDK (one assembly, PackageId ToSpec.Sdk)
tests/ToSpec.Sdk.Tests/            xunit v3 guarantee suite + the sdk-protocol fixture generator
tests/ToSpec.Sdk.Tests/fixtures/   vendored copy of the sdk-protocol golden files (canonical source: sdk-protocol)
benchmarks/ToSpec.Sdk.Benchmarks/  BenchmarkDotNet request-path overhead harness
```

## Testing

- Framework **xunit v3**. Naming `MethodOrBehavior_Condition_ExpectedOutcome`; classes
  `<TypeUnderTest>Tests`. Tests are **in-process** (ASP.NET `TestServer` + a stub
  `HttpMessageHandler`) — no Docker, no real network, deterministic.
- The guarantee suite is not optional — every guarantee above has a test, and the structural
  redaction test scans the real outbound wire bytes.
- No live external calls. The bench harness is built in CI but not run; numbers live in the
  README from local runs.

## Build / run

```sh
dotnet build -warnaserror
dotnet test
dotnet run -c Release --project benchmarks/ToSpec.Sdk.Benchmarks -- --filter '*'
```
