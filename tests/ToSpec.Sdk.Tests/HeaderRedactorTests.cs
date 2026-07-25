using Microsoft.AspNetCore.Http;
using ToSpec.Redact;
using ToSpec.Sdk.Tests.Support;

namespace ToSpec.Sdk.Tests;

/// <summary>Header strip/hash mirrors the platform gateway: auth-shaped headers are
/// stripped unconditionally (no ruleset required), ruleset lists add strip/hash on top,
/// and hashed headers become deterministic <c>tsr_v{n}_…</c> tokens.</summary>
public sealed class HeaderRedactorTests
{
    private static readonly RedactionKeyring Keys = new(new byte[32], hmacKeyVersion: 1);

    [Fact]
    public void Redact_StripsAuthDefaults_EvenWithNoRuleset()
    {
        var headers = new HeaderDictionary
        {
            ["Authorization"] = "Bearer secret",
            ["Cookie"] = "sid=abc",
            ["Accept"] = "application/json",
        };

        Dictionary<string, string> result = HeaderRedactor.Redact(headers, isRequest: true, rules: null, Keys);

        Assert.False(result.ContainsKey("Authorization"));
        Assert.False(result.ContainsKey("Cookie"));
        Assert.Equal("application/json", result["Accept"]);
    }

    [Fact]
    public void Redact_HashesRulesetHashHeaders_Deterministically()
    {
        CompiledRuleset rules = Rulesets.Compile(
            """
            headers:
              hash: [X-Client-Id]
            """);
        var headers = new HeaderDictionary { ["X-Client-Id"] = "client-42" };

        Dictionary<string, string> result = HeaderRedactor.Redact(headers, isRequest: true, rules, Keys);

        Assert.StartsWith("tsr_v1_", result["X-Client-Id"], StringComparison.Ordinal);
        Assert.DoesNotContain("client-42", result["X-Client-Id"], StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_ResponseDefaults_StripSetCookie()
    {
        var headers = new HeaderDictionary { ["Set-Cookie"] = "sid=abc", ["Content-Type"] = "application/json" };

        Dictionary<string, string> result = HeaderRedactor.Redact(headers, isRequest: false, rules: null, Keys);

        Assert.False(result.ContainsKey("Set-Cookie"));
        Assert.Equal("application/json", result["Content-Type"]);
    }
}
