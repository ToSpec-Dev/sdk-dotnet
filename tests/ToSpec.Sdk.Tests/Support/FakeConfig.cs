using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace ToSpec.Sdk.Tests.Support;

/// <summary>
/// A mutable stand-in for the platform's <c>GET /v1/sdk/config</c>. It serves the config
/// body (snake_case, <c>compiled</c> omitted when null) with an <c>ETag</c>, and answers a
/// matching <c>If-None-Match</c> with 304 — exactly like the real endpoint, so the SDK's
/// conditional-GET path is exercised end-to-end. Mutating a field and calling
/// <see cref="Bump"/> moves the ETag so the next poll sees a change within one interval.
/// </summary>
internal sealed class FakeConfig
{
    private int _version = 1;

    public int RulesetVersion { get; set; }

    /// <summary>Raw compiled-ruleset jsonb (schema v1), or null for "no ruleset published".</summary>
    public string? CompiledJson { get; set; }

    public string SamplingJson { get; set; } = "{}";

    public bool KillSwitch { get; set; }

    public string ETag { get; private set; } = "\"cfg-1\"";

    /// <summary>Advance the ETag so the next poll is treated as changed.</summary>
    public void Bump() => ETag = $"\"cfg-{++_version}\"";

    public HttpResponseMessage Respond(HttpRequestMessage request)
    {
        string? ifNoneMatch = request.Headers.IfNoneMatch.FirstOrDefault()?.ToString();
        if (ifNoneMatch == ETag)
        {
            var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
            notModified.Headers.ETag = EntityTagHeaderValue.Parse(ETag);
            return notModified;
        }

        var sb = new StringBuilder();
        sb.Append("{\"ruleset_version\":").Append(RulesetVersion);
        if (CompiledJson is not null)
        {
            sb.Append(",\"compiled\":").Append(CompiledJson);
        }

        sb.Append(",\"sampling_rules\":").Append(SamplingJson);
        sb.Append(",\"kill_switch\":").Append(KillSwitch ? "true" : "false");
        sb.Append('}');

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json"),
        };
        response.Headers.ETag = EntityTagHeaderValue.Parse(ETag);
        return response;
    }
}
