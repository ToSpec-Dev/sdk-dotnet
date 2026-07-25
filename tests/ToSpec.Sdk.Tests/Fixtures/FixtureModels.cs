using ToSpec.Sdk.Wire;

namespace ToSpec.Sdk.Tests.Fixtures;

/// <summary>A deterministic HMAC token vector: value + key ⇒ <c>tsr_v{n}_…</c> token.</summary>
internal sealed record TokenVector(string Name, string KeyHex, int KeyVersion, string Value, string Token);

/// <summary>
/// A golden redaction vector — the cross-language byte-parity anchor (any SDK port must
/// produce <see cref="BodyOut"/> from <see cref="BodyIn"/> given the compiled ruleset and
/// key, or the exact <see cref="HeadersOut"/> for header vectors). <see cref="Malformed"/>
/// marks the "engine rejects it → the body is dropped" case (<see cref="BodyOut"/> null).
/// </summary>
internal sealed record RedactionVector
{
    public required string Name { get; init; }

    /// <summary>"body" or "headers".</summary>
    public required string Kind { get; init; }

    public required string CompiledRulesetJson { get; init; }

    public required string HmacKeyHex { get; init; }

    public required int HmacKeyVersion { get; init; }

    // Body vectors.
    public string? ContentFormat { get; init; }

    public string? BodyIn { get; init; }

    public string? BodyOut { get; init; }

    public bool Malformed { get; init; }

    // Header vectors.
    public bool IsRequest { get; init; }

    public IReadOnlyDictionary<string, string>? HeadersIn { get; init; }

    public IReadOnlyDictionary<string, string>? HeadersOut { get; init; }
}

/// <summary>A golden signed batch: the identity-encoded <see cref="CanonicalJson"/> is the
/// exact string signed, and <see cref="Signature"/> is
/// <c>sha256=hexlower(HMAC-SHA256(utf8(IngestKey), utf8(CanonicalJson)))</c>. The typed
/// <see cref="Batch"/> is retained for validation (serialize it → must equal
/// <see cref="CanonicalJson"/>); it is not written to the fixture file.</summary>
internal sealed record BatchVector(string Name, string IngestKey, IngestBatch Batch, string CanonicalJson, string Signature);

/// <summary>Everything the generator produces, in one deterministic bundle.</summary>
internal sealed record FixtureSet(
    IReadOnlyList<TokenVector> Tokens,
    IReadOnlyList<RedactionVector> Redactions,
    IReadOnlyList<BatchVector> Batches);
