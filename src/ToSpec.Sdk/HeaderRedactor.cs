using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using ToSpec.Redact;

namespace ToSpec.Sdk;

/// <summary>
/// Header strip/hash for capture, ported from the platform gateway's
/// <c>RedactedHeaderSerializer</c> (SPEC-cert-gateway-architecture §5.1). The platform
/// default strip sets are <b>unconditional</b> — they apply with no ruleset at all, so a
/// raw <c>Authorization</c> can never be transmitted regardless of tenant configuration.
/// Ruleset <c>Headers.Strip</c>/<c>Headers.Hash</c> lists <b>add to</b>, never replace,
/// the defaults; hashed headers use the same deterministic <c>tsr_v{n}_…</c> tokenizer as
/// the body engine, so a value hashed here joins the same value hashed in a body.
/// </summary>
public static class HeaderRedactor
{
    private static readonly FrozenSet<string> RequestDefaults = new[]
    {
        "Authorization", "X-Api-Key", "Cookie", "Proxy-Authorization", "X-ToSpec-Key",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> ResponseDefaults = new[]
    {
        "Set-Cookie", "WWW-Authenticate", "Proxy-Authenticate",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <param name="isRequest">true selects the request default strip set, false the response set.</param>
    public static Dictionary<string, string> Redact(
        IHeaderDictionary headers, bool isRequest, CompiledRuleset? rules, RedactionKeyring keys)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(keys);

        FrozenSet<string> platformStrip = isRequest ? RequestDefaults : ResponseDefaults;
        CompiledHeaderRules? headerRules = rules?.Headers;

        var result = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header in headers)
        {
            if (platformStrip.Contains(header.Key) || headerRules?.Strip.Contains(header.Key) is true)
            {
                continue;
            }

            result[header.Key] = headerRules?.Hash.Contains(header.Key) is true
                ? HmacTokenizer.Tokenize(header.Value.ToString().AsSpan(), keys)
                : header.Value.ToString();
        }

        return result;
    }
}
