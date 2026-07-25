using System.Text.Json;

namespace ToSpec.Sdk.Wire;

/// <summary>
/// The <c>GET /v1/sdk/config</c> response body (snake_case). <c>compiled</c> is the raw
/// jsonb of the compiled redaction ruleset (schema v1) — passed to
/// <c>ToSpec.Redact.CompiledRulesetSerializer.Deserialize</c> verbatim; it is omitted
/// entirely when no ruleset is published (so the field is nullable here).
/// <c>sampling_rules</c> is a jsonb object, e.g. <c>{"errors":100,"success":5}</c>.
/// </summary>
public sealed record SdkConfigResponse
{
    public int RulesetVersion { get; init; }

    public JsonElement? Compiled { get; init; }

    public JsonElement? SamplingRules { get; init; }

    public bool KillSwitch { get; init; }
}
