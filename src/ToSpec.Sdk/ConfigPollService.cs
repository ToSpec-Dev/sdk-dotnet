using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using ToSpec.Redact;
using ToSpec.Sdk.Wire;

namespace ToSpec.Sdk;

/// <summary>
/// Polls <c>GET /v1/sdk/config</c> (SPEC-cert-gateway-architecture §9) and swaps the
/// <see cref="ConformanceState"/> snapshot atomically. Steady-state polls send
/// <c>If-None-Match</c> and get a cheap 304 (no snapshot change). A change — a new ruleset
/// version, a sampling edit, or the <b>kill switch</b> — comes back as a 200 and is applied
/// within one poll interval. Every fault is swallowed and the last-good snapshot is kept:
/// a poll that fails, returns garbage, or carries an unknown ruleset schema never degrades
/// what the SDK is already doing.
/// </summary>
internal sealed class ConfigPollService : BackgroundService
{
    private readonly ToSpecConformanceOptions _options;
    private readonly ConformanceState _state;
    private readonly ConformanceMetrics _metrics;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Uri _configUri;
    private readonly string _ingestKey;

    public ConfigPollService(
        ToSpecConformanceOptions options,
        ConformanceState state,
        ConformanceMetrics metrics,
        IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _state = state;
        _metrics = metrics;
        _httpClientFactory = httpClientFactory;
        _configUri = new Uri(options.IngestBaseUrl!, ToSpecConformance.ConfigPath);
        _ingestKey = options.IngestKey!;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Poll once immediately (this runs in the background, so it never blocks host
        // startup), then on the interval.
        await PollOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(_options.ConfigPollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await PollOnceAsync(stoppingToken);
        }
    }

    private async Task PollOnceAsync(CancellationToken stoppingToken)
    {
        _metrics.IncConfigPoll();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _configUri);
            request.Headers.TryAddWithoutValidation(IngestSigner.IngestKeyHeader, _ingestKey);

            string? etag = _state.Current.ETag;
            if (etag is not null)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(_options.IngestTimeout);

            HttpClient client = _httpClientFactory.CreateClient(ToSpecConformance.HttpClientName);
            using HttpResponseMessage response = await client.SendAsync(request, timeout.Token);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                _metrics.IncConfigNotModified();
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                FaultReporter.Report(_options, new ConformanceFault(
                    ConformanceFaultKind.ConfigPoll, $"config poll returned {(int)response.StatusCode}", null));
                return;
            }

            string body = await response.Content.ReadAsStringAsync(timeout.Token);
            SdkConfigResponse? dto = JsonSerializer.Deserialize(body, SdkJsonContext.Default.SdkConfigResponse);
            if (dto is null)
            {
                FaultReporter.Report(_options, new ConformanceFault(
                    ConformanceFaultKind.ConfigPoll, "config response was empty", null));
                return;
            }

            // Ruleset compatibility is independent from the emergency controls. If an
            // older SDK cannot read new ruleset vocabulary, retain only its last-good
            // ruleset while still applying kill-switch, sampling and the response ETag.
            ConformanceSnapshot current = _state.Current;
            CompiledRuleset? ruleset = null;
            int rulesetVersion = dto.RulesetVersion;
            if (dto.Compiled is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } compiled)
            {
                try
                {
                    ruleset = CompiledRulesetSerializer.Deserialize(compiled.GetRawText());
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    ruleset = current.Ruleset;
                    rulesetVersion = current.RulesetVersion;
                    FaultReporter.Report(_options, new ConformanceFault(
                        ConformanceFaultKind.ConfigPoll,
                        "compiled ruleset is unsupported; retained last-good ruleset while applying controls",
                        ex));
                }
            }

            string? newEtag = response.Headers.ETag?.ToString() ?? current.ETag;
            _state.Update(new ConformanceSnapshot
            {
                Ruleset = ruleset,
                RulesetVersion = rulesetVersion,
                Sampling = SamplingRule.Parse(dto.SamplingRules),
                KillSwitch = dto.KillSwitch,
                ETag = newEtag,
            });
            _metrics.SetKillSwitch(dto.KillSwitch);
            _metrics.IncConfigChange();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // Keep the last-good snapshot; report and move on.
            FaultReporter.Report(_options, new ConformanceFault(ConformanceFaultKind.ConfigPoll, ex.Message, ex));
        }
    }
}
