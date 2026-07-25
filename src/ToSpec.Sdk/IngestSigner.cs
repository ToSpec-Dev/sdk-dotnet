using System.Security.Cryptography;

namespace ToSpec.Sdk;

/// <summary>
/// HMAC-SHA256 signing of the ingest batch body — the SDK-side twin of the platform's
/// <c>ToSpec.Core.Security.EmitSigning</c> (the SDK cannot reference platform code, so it
/// reimplements the identical recipe over the BCL). The signed message is the <b>exact
/// bytes put on the wire</b> (post-gzip when gzipping — the server verifies before it
/// decompresses); the secret is the raw ingest-key string, UTF-8 encoded. Header value is
/// <c>sha256=&lt;lowercase-hex&gt;</c>. This recipe is pinned by the
/// <c>ToSpec-Dev/sdk-protocol</c> signed-batch fixtures.
/// </summary>
public static class IngestSigner
{
    /// <summary>Bearer credential + HMAC secret (raw <c>tsp_ing_…</c> key).</summary>
    public const string IngestKeyHeader = "X-ToSpec-Ingest-Key";

    /// <summary>Signature header: <c>sha256=&lt;hex&gt;</c>.</summary>
    public const string SignatureHeader = "X-ToSpec-Signature";

    /// <summary><c>"sha256=" + hexlower(HMAC-SHA256(key, payload))</c>.</summary>
    public static string Sign(byte[] keyUtf8, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(keyUtf8);
        return "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(keyUtf8, payload));
    }
}
