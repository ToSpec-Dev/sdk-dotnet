namespace ToSpec.Sdk;

/// <summary>User fault hooks are observability only. A broken hook must never turn a
/// swallowed SDK fault into a host request or background-service failure.</summary>
internal static class FaultReporter
{
    public static void Report(ToSpecConformanceOptions options, ConformanceFault fault)
    {
        try
        {
            options.OnFault?.Invoke(fault);
        }
        catch
        {
            // There is intentionally no second callback/logging dependency here: it
            // could recurse into the same untrusted hook. Metrics for the originating
            // SDK fault are recorded by its caller.
        }
    }
}
