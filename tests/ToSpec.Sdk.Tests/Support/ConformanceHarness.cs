using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ToSpec.Sdk.Tests.Support;

/// <summary>
/// An in-process host wired with the conformance middleware and a stub ingest handler — no
/// Docker, no real network. Tests drive requests through <see cref="Client"/> and inspect
/// what the SDK sent through <see cref="Ingest"/>, plus the live <see cref="Metrics"/> and
/// <see cref="State"/>. Required options are pre-filled with safe test defaults; the test's
/// <c>configure</c> overrides only what it cares about.
/// </summary>
internal sealed class ConformanceHarness : IAsyncDisposable
{
    public static readonly Guid TestPartnerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IHost _host;

    private ConformanceHarness(IHost host, RecordingHandler ingest)
    {
        _host = host;
        Ingest = ingest;
        Client = host.GetTestClient();
    }

    public HttpClient Client { get; }

    public RecordingHandler Ingest { get; }

    public IServiceProvider Services => _host.Services;

    public ConformanceMetrics Metrics => Services.GetRequiredService<ConformanceMetrics>();

    public ConformanceState State => Services.GetRequiredService<ConformanceState>();

    public static async Task<ConformanceHarness> StartAsync(
        Action<ToSpecConformanceOptions> configure,
        RecordingHandler ingest,
        RequestDelegate? app = null,
        CancellationToken cancellationToken = default)
    {
        RequestDelegate pipeline = app ?? DefaultApp;

        IHost host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddToSpecConformance(options =>
                    {
                        options.IngestBaseUrl = new Uri("https://ingest.test.local");
                        options.IngestKey = "tsp_ing_test_key";
                        options.RedactionKey = new byte[32];
                        options.RedactionKeyVersion = 1;
                        options.ResolvePartnerId = _ => TestPartnerId;
                        options.ConfigPollInterval = TimeSpan.FromMilliseconds(100);
                        options.FlushInterval = TimeSpan.FromMilliseconds(50);
                        configure(options);
                    });

                    // Route the SDK's outbound calls to the stub handler.
                    services.AddHttpClient(ToSpecConformance.HttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(() => ingest);
                });
                web.Configure(builder =>
                {
                    builder.UseToSpecConformance();
                    builder.Run(pipeline);
                });
            })
            .Build();

        await host.StartAsync(cancellationToken);
        return new ConformanceHarness(host, ingest);
    }

    /// <summary>Default endpoint: consume the request body (so request buffering is
    /// exercised) and echo a small JSON response.</summary>
    private static async Task DefaultApp(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        _ = await reader.ReadToEndAsync(context.RequestAborted);
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("""{"ok":true}""", context.RequestAborted);
    }

    /// <summary>Polls <paramref name="condition"/> until true or the timeout elapses.</summary>
    public async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string because)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        if (!condition())
        {
            throw new TimeoutException($"Timed out waiting for: {because}. Metrics: {Metrics.Snapshot()}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync(TimeSpan.FromSeconds(2));
        _host.Dispose();
    }
}
