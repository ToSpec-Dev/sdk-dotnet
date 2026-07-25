using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ToSpec.Redact;
using ToSpec.Sdk.Tests.Fixtures;
using ToSpec.Sdk.Tests.Support;
using ToSpec.Sdk.Wire;

namespace ToSpec.Sdk.Tests;

/// <summary>
/// Proves the SDK passes the <c>ToSpec-Dev/sdk-protocol</c> golden fixtures — token vectors,
/// redaction vectors (JSON/XML/headers/malformed), and signed batches — using the real
/// engine and signer. These are the exact criteria a community port (the PHP author) runs
/// to certify: same key + input ⇒ same token / same redacted bytes / same signature.
/// </summary>
public sealed class ProtocolFixturesTests
{
    private static readonly FixtureSet Set = FixtureFactory.BuildAll();

    [Fact]
    public void TokenVectors_ReproduceGoldenTokens()
    {
        Assert.NotEmpty(Set.Tokens);
        foreach (TokenVector t in Set.Tokens)
        {
            var keyring = new RedactionKeyring(FixtureFactory.FromHex(t.KeyHex), t.KeyVersion);
            string actual = HmacTokenizer.Tokenize(t.Value.AsSpan(), keyring);
            Assert.Equal(t.Token, actual);
        }
    }

    [Fact]
    public void BodyRedactionVectors_ReproduceGoldenOutput()
    {
        foreach (RedactionVector v in Set.Redactions.Where(v => v.Kind == "body"))
        {
            string? actual = FixtureFactory.RedactBody(
                v.CompiledRulesetJson, v.ContentFormat!, v.BodyIn!, FixtureFactory.FromHex(v.HmacKeyHex), v.HmacKeyVersion);

            if (v.Malformed)
            {
                Assert.Null(actual);
            }
            else
            {
                Assert.Equal(v.BodyOut, actual);
            }
        }
    }

    [Fact]
    public void BinaryMalformedVectors_AreRejectedWithoutUtf8Normalization()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "malformed.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        CompiledRuleset rules = Rulesets.Compile("body: []\n");
        var keys = new RedactionKeyring(new byte[32], 1);
        foreach (JsonElement vector in document.RootElement.GetProperty("vectors").EnumerateArray())
        {
            byte[] body = Convert.FromBase64String(vector.GetProperty("body_base64").GetString()!);
            IBodyRedactor redactor = BodyRedactorRegistry.Resolve(
                vector.GetProperty("content_format").GetString()!)!;
            var output = new System.Buffers.ArrayBufferWriter<byte>();
            RedactionResult result = redactor.Redact(
                new System.Buffers.ReadOnlySequence<byte>(body), output, rules, keys);
            Assert.Equal(RedactionStatus.MalformedInput, result.Status);
        }
    }

    [Fact]
    public void HeaderRedactionVectors_ReproduceGoldenOutput()
    {
        foreach (RedactionVector v in Set.Redactions.Where(v => v.Kind == "headers"))
        {
            CompiledRuleset ruleset = CompiledRulesetSerializer.Deserialize(v.CompiledRulesetJson);
            var keyring = new RedactionKeyring(FixtureFactory.FromHex(v.HmacKeyHex), v.HmacKeyVersion);
            var headers = new HeaderDictionary();
            foreach ((string key, string value) in v.HeadersIn!)
            {
                headers[key] = value;
            }

            Dictionary<string, string> actual = HeaderRedactor.Redact(headers, v.IsRequest, ruleset, keyring);

            Assert.Equal(
                v.HeadersOut!.OrderBy(p => p.Key, StringComparer.Ordinal),
                actual.OrderBy(p => p.Key, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void SignedBatches_SerializeToCanonicalJson_AndSignatureMatches()
    {
        Assert.NotEmpty(Set.Batches);
        foreach (BatchVector b in Set.Batches)
        {
            string reserialized = JsonSerializer.Serialize(b.Batch, SdkJsonContext.Default.IngestBatch);
            Assert.Equal(b.CanonicalJson, reserialized);

            string signature = IngestSigner.Sign(Encoding.UTF8.GetBytes(b.IngestKey), Encoding.UTF8.GetBytes(b.CanonicalJson));
            Assert.Equal(b.Signature, signature);
        }
    }
}
