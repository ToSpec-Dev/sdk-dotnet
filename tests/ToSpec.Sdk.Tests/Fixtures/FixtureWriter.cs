using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToSpec.Sdk.Tests.Fixtures;

/// <summary>
/// Renders a <see cref="FixtureSet"/> to the on-disk golden-file layout (stable key order,
/// 2-space indent, <c>\n</c> line endings) and writes it. <see cref="Render"/> is the
/// single source of file content, used both to write the committed files and to assert they
/// are up to date — so the two can never diverge silently.
/// </summary>
internal static class FixtureWriter
{
    // Relaxed encoder so quotes render as \" (not ") — the golden files are read by
    // humans and ported by hand into other languages, so keep them legible.
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>(relative path, file content) for every fixture file.</summary>
    public static IEnumerable<(string Path, string Content)> Render(FixtureSet set)
    {
        yield return ("manifest.json", RenderManifest(set));
        yield return ("tokens.json", RenderTokens(set.Tokens));

        foreach (RedactionVector vector in set.Redactions)
        {
            yield return ($"redaction/{vector.Name}.json", RenderRedaction(vector));
        }

        foreach (BatchVector batch in set.Batches)
        {
            yield return ($"batches/{batch.Name}.json", RenderBatch(batch));
        }
    }

    public static void Write(string baseDir, FixtureSet set)
    {
        foreach ((string path, string content) in Render(set))
        {
            string full = Path.Combine(baseDir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }

    private static string RenderManifest(FixtureSet set)
    {
        var node = new JsonObject
        {
            ["description"] = "ToSpec SDK conformance fixtures. See PROTOCOL.md and README.md.",
            ["schema"] = new JsonObject
            {
                ["redaction"] = 1,
                ["token"] = 1,
                ["batch"] = 1,
                ["edge"] = 1,
            },
            ["tokens"] = "tokens.json",
            ["malformed"] = "malformed.json",
            ["edge_cases"] = "edge-cases.json",
            ["redaction"] = new JsonArray(set.Redactions.Select(v => (JsonNode)$"redaction/{v.Name}.json").ToArray()),
            ["batches"] = new JsonArray(set.Batches.Select(b => (JsonNode)$"batches/{b.Name}.json").ToArray()),
        };
        return Serialize(node);
    }

    private static string RenderTokens(IReadOnlyList<TokenVector> tokens)
    {
        var array = new JsonArray();
        foreach (TokenVector t in tokens)
        {
            array.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["key_hex"] = t.KeyHex,
                ["key_version"] = t.KeyVersion,
                ["value"] = t.Value,
                ["token"] = t.Token,
            });
        }

        return Serialize(array);
    }

    private static string RenderRedaction(RedactionVector v)
    {
        var node = new JsonObject
        {
            ["name"] = v.Name,
            ["kind"] = v.Kind,
            ["compiled_ruleset"] = JsonNode.Parse(v.CompiledRulesetJson),
            ["hmac_key_hex"] = v.HmacKeyHex,
            ["hmac_key_version"] = v.HmacKeyVersion,
        };

        if (v.Kind == "body")
        {
            node["content_format"] = v.ContentFormat;
            node["body_in"] = v.BodyIn;
            node["body_out"] = v.BodyOut;
            node["malformed"] = v.Malformed;
        }
        else
        {
            node["is_request"] = v.IsRequest;
            node["headers_in"] = ToJson(v.HeadersIn);
            node["headers_out"] = ToJson(v.HeadersOut);
        }

        return Serialize(node);
    }

    private static string RenderBatch(BatchVector b)
    {
        var node = new JsonObject
        {
            ["name"] = b.Name,
            ["ingest_key"] = b.IngestKey,
            ["canonical_json"] = b.CanonicalJson,
            ["signature"] = b.Signature,
        };
        return Serialize(node);
    }

    private static JsonObject? ToJson(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return null;
        }

        var obj = new JsonObject();
        foreach ((string key, string value) in headers.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            obj[key] = value;
        }

        return obj;
    }

    private static string Serialize(JsonNode node) =>
        node.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n";
}
