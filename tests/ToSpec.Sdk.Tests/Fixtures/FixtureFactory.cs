using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ToSpec.Redact;
using ToSpec.Sdk.Tests.Support;
using ToSpec.Sdk.Wire;

namespace ToSpec.Sdk.Tests.Fixtures;

/// <summary>
/// The reference generator for the <c>ToSpec-Dev/sdk-protocol</c> fixtures. It uses the real
/// SDK and ToSpec.Redact code paths, with fixed keys / ids / timestamps (no clock, no RNG),
/// so its output is byte-deterministic — the committed golden files are exactly what this
/// produces, and any behavioural drift changes them (and fails a test).
/// </summary>
internal static class FixtureFactory
{
    private const string ZeroKeyHex = "0000000000000000000000000000000000000000000000000000000000000000";
    private const string SeqKeyHex = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";

    private const string FixtureIngestKey = "tsp_ing_fixturekey00000000000000000000000000";

    private static readonly Guid BatchId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid EventId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid PartnerId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset FixtureTs = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static FixtureSet BuildAll() => new(
        BuildTokens(),
        BuildRedactions(),
        BuildBatches());

    private static IReadOnlyList<TokenVector> BuildTokens() =>
    [
        Token("pan_zero_key_v1", ZeroKeyHex, 1, "4111111111111111"),
        Token("email_zero_key_v1", ZeroKeyHex, 1, "jane.doe@example.com"),
        Token("pan_seq_key_v2", SeqKeyHex, 2, "4111111111111111"),
    ];

    private static IReadOnlyList<RedactionVector> BuildRedactions() =>
    [
        Body("json_hash_pan", "json",
            "body:\n  - { path: \"$.payment.cardNumber\", action: hash }",
            """{"payment":{"cardNumber":"4111111111111111"}}"""),

        Body("json_mask_email_domain", "json",
            "body:\n  - { path: \"$.guest.email\", action: mask, keep: domain }",
            """{"guest":{"email":"jane.doe@example.com"}}"""),

        Body("json_mask_last4", "json",
            "body:\n  - { path: \"$.card\", action: mask, keep: last4 }",
            """{"card":"4111111111111111"}"""),

        Body("json_drop_password", "json",
            "body:\n  - { path: \"$..password\", action: drop }",
            """{"user":{"password":"hunter2"},"password":"top"}"""),

        Body("json_freetext_detect", "json",
            "freetext:\n  scan_unknown: true\n  detectors: [pan_luhn, email]\ndefaults:\n  unknown_pii_policy: detect_and_hash",
            """{"note":"charge card 4111111111111111 to jane.doe@example.com"}"""),

        Body("xml_hash", "xml",
            "body:\n  - { path: \"$.order.card\", action: hash }",
            "<order><card>4111111111111111</card></order>"),

        Malformed("json_malformed_dropped",
            "body:\n  - { path: \"$.card\", action: hash }",
            """{"card":"4111111111111111"""),

        Headers("headers_request_strip_and_hash",
            "headers:\n  hash: [X-Client-Id]",
            isRequest: true,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer sekret",
                ["X-Client-Id"] = "client-42",
                ["Accept"] = "application/json",
            }),
    ];

    private static IReadOnlyList<BatchVector> BuildBatches()
    {
        var metadataOnly = new IngestBatch
        {
            BatchId = BatchId,
            Events =
            [
                new IngestEventEnvelope
                {
                    EventId = EventId,
                    PartnerId = PartnerId,
                    Ts = FixtureTs,
                    Direction = "inbound",
                    Method = "POST",
                    Path = "/v1/reservations",
                    Status = 201,
                    LatencyMs = 17,
                    ReqHeaders = new Dictionary<string, string> { ["Accept"] = "application/json" },
                    RespHeaders = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                    ReqSize = 42,
                    RespSize = 128,
                    ContentFormat = "json",
                    RedactionVersion = 3,
                },
            ],
        };

        // A body-carrying batch: the request body is the already-redacted (hashed-PAN)
        // output — i.e. exactly what redaction-before-transmission produces.
        string redacted = RedactBody(
            Rulesets.CompiledJson("body:\n  - { path: \"$.payment.cardNumber\", action: hash }"),
            "json",
            """{"payment":{"cardNumber":"4111111111111111"}}""",
            FromHex(ZeroKeyHex),
            1)!;

        var withBodies = new IngestBatch
        {
            BatchId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"),
            Events =
            [
                new IngestEventEnvelope
                {
                    EventId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"),
                    PartnerId = PartnerId,
                    Ts = FixtureTs,
                    Direction = "inbound",
                    Method = "POST",
                    Path = "/v1/payments",
                    Status = 200,
                    LatencyMs = 33,
                    ReqHeaders = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                    RespHeaders = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                    ReqBody = Convert.ToBase64String(Encoding.UTF8.GetBytes(redacted)),
                    ReqSize = 46,
                    ContentFormat = "json",
                    RedactionVersion = 3,
                },
            ],
        };

        return
        [
            Batch("batch_metadata_only", metadataOnly),
            Batch("batch_with_redacted_body", withBodies),
        ];
    }

    private static TokenVector Token(string name, string keyHex, int version, string value)
    {
        var keyring = new RedactionKeyring(FromHex(keyHex), version);
        return new TokenVector(name, keyHex, version, value, HmacTokenizer.Tokenize(value.AsSpan(), keyring));
    }

    private static RedactionVector Body(string name, string format, string yaml, string bodyIn)
    {
        string compiled = Rulesets.CompiledJson(yaml);
        string? bodyOut = RedactBody(compiled, format, bodyIn, FromHex(ZeroKeyHex), 1);
        return new RedactionVector
        {
            Name = name,
            Kind = "body",
            ContentFormat = format,
            CompiledRulesetJson = compiled,
            HmacKeyHex = ZeroKeyHex,
            HmacKeyVersion = 1,
            BodyIn = bodyIn,
            BodyOut = bodyOut,
        };
    }

    private static RedactionVector Malformed(string name, string yaml, string bodyIn) => new()
    {
        Name = name,
        Kind = "body",
        ContentFormat = "json",
        CompiledRulesetJson = Rulesets.CompiledJson(yaml),
        HmacKeyHex = ZeroKeyHex,
        HmacKeyVersion = 1,
        BodyIn = bodyIn,
        BodyOut = null,
        Malformed = true,
    };

    private static RedactionVector Headers(
        string name, string yaml, bool isRequest, IReadOnlyDictionary<string, string> headersIn)
    {
        CompiledRuleset ruleset = Rulesets.Compile(yaml);
        var keyring = new RedactionKeyring(FromHex(ZeroKeyHex), 1);
        var dictionary = new HeaderDictionary();
        foreach ((string key, string value) in headersIn)
        {
            dictionary[key] = value;
        }

        Dictionary<string, string> headersOut = HeaderRedactor.Redact(dictionary, isRequest, ruleset, keyring);

        return new RedactionVector
        {
            Name = name,
            Kind = "headers",
            CompiledRulesetJson = Rulesets.CompiledJson(yaml),
            HmacKeyHex = ZeroKeyHex,
            HmacKeyVersion = 1,
            IsRequest = isRequest,
            HeadersIn = headersIn,
            HeadersOut = headersOut,
        };
    }

    private static BatchVector Batch(string name, IngestBatch batch)
    {
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(batch, SdkJsonContext.Default.IngestBatch);
        string canonicalJson = Encoding.UTF8.GetString(canonical);
        string signature = IngestSigner.Sign(Encoding.UTF8.GetBytes(FixtureIngestKey), canonical);
        return new BatchVector(name, FixtureIngestKey, batch, canonicalJson, signature);
    }

    /// <summary>Applies a compiled ruleset (in its jsonb wire form) to a body; returns null
    /// when the engine reports malformed input (⇒ the SDK drops the body).</summary>
    public static string? RedactBody(string compiledRulesetJson, string format, string bodyIn, byte[] key, int version)
    {
        CompiledRuleset ruleset = CompiledRulesetSerializer.Deserialize(compiledRulesetJson);
        var keyring = new RedactionKeyring(key, version);
        IBodyRedactor redactor = BodyRedactorRegistry.Resolve(format)
            ?? throw new InvalidOperationException($"no redactor for format '{format}'");

        var input = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(bodyIn));
        var output = new ArrayBufferWriter<byte>(bodyIn.Length + 256);
        RedactionResult result = redactor.Redact(input, output, ruleset, keyring);
        return result.Status == RedactionStatus.Rewritten
            ? Encoding.UTF8.GetString(output.WrittenSpan)
            : null;
    }

    public static byte[] FromHex(string hex) => Convert.FromHexString(hex);
}
