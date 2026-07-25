using System.Text.Json;

namespace ToSpec.Sdk;

/// <summary>
/// SDK-side sampling (SPEC-cert-gateway-architecture §9: "sampling config, e.g. 100%
/// errors, 5% success"). The server distributes this as an opaque jsonb object on the
/// config response — <c>{"errors":100,"success":5}</c> — and does not enforce it; the SDK
/// does. Percentages are 0–100 and clamped. A missing/blank field defaults to 100 (emit
/// everything) so a misconfigured tenant never silently stops capturing.
/// </summary>
public readonly record struct SamplingRule(int ErrorPercent, int SuccessPercent)
{
    /// <summary>Capture everything — the safe default before any config is fetched.</summary>
    public static readonly SamplingRule CaptureAll = new(100, 100);

    /// <summary>Reads <c>errors</c>/<c>success</c> from the config's <c>sampling_rules</c>
    /// jsonb; anything unparseable falls back to <see cref="CaptureAll"/>.</summary>
    public static SamplingRule Parse(JsonElement? samplingRules)
    {
        if (samplingRules is not { ValueKind: JsonValueKind.Object } obj)
        {
            return CaptureAll;
        }

        return new SamplingRule(
            ReadPercent(obj, "errors"),
            ReadPercent(obj, "success"));
    }

    private static int ReadPercent(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int percent))
        {
            return Math.Clamp(percent, 0, 100);
        }

        return 100;
    }
}
