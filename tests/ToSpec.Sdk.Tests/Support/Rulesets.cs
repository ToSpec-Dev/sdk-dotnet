using ToSpec.Redact;

namespace ToSpec.Sdk.Tests.Support;

/// <summary>Compiles YAML rulesets to the same jsonb the platform config endpoint serves,
/// so tests feed the SDK exactly what production would.</summary>
internal static class Rulesets
{
    public static CompiledRuleset Compile(string yaml)
    {
        RulesetCompileResult result = RedactionRulesetCompiler.Compile(yaml);
        return result.Ruleset
            ?? throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => $"{e.Location}: {e.Message}")));
    }

    public static string CompiledJson(string yaml) => CompiledRulesetSerializer.Serialize(Compile(yaml));
}
