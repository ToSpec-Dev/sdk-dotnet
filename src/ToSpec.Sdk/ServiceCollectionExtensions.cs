using ToSpec.Redact;
using ToSpec.Sdk;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration for the ToSpec conformance SDK.</summary>
public static class ToSpecConformanceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the conformance pipeline: options (validated eagerly), the shared
    /// redaction keyring, the bounded queue, the config poller, and the batch sender.
    /// Pair with <c>app.UseToSpecConformance()</c> to install the middleware. Resolve
    /// <see cref="ConformanceMetrics"/> from DI to export the SDK's counters.
    /// </summary>
    public static IServiceCollection AddToSpecConformance(
        this IServiceCollection services, Action<ToSpecConformanceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ToSpecConformanceOptions();
        configure(options);
        options.Validate();

        services.AddSingleton(options);
        services.AddSingleton(new RedactionKeyring(options.RedactionKey!, options.RedactionKeyVersion));
        services.AddSingleton<ConformanceMetrics>();
        services.AddSingleton<ConformanceState>();
        services.AddSingleton<Sampler>();
        services.AddSingleton(sp =>
            new ConformanceChannel(
                options.QueueCapacity, options.MaxQueueBytes, sp.GetRequiredService<ConformanceMetrics>()));

        services.AddHttpClient(ToSpecConformance.HttpClientName);

        services.AddHostedService<ConfigPollService>();
        services.AddHostedService<BatchSenderService>();

        return services;
    }
}
