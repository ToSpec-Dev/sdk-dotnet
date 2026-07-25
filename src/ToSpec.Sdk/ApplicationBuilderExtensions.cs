using ToSpec.Sdk;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Middleware installation for the ToSpec conformance SDK.</summary>
public static class ToSpecConformanceApplicationBuilderExtensions
{
    /// <summary>
    /// Installs <see cref="ToSpecConformanceMiddleware"/>. Place it early in the pipeline
    /// (before endpoint execution) so it observes the full request/response. Requires
    /// <c>services.AddToSpecConformance(...)</c>.
    /// </summary>
    public static IApplicationBuilder UseToSpecConformance(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<ToSpecConformanceMiddleware>();
    }
}
