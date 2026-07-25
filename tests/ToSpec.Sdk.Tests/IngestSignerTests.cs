using System.Security.Cryptography;
using System.Text;

namespace ToSpec.Sdk.Tests;

/// <summary>The signature recipe matches the platform's <c>EmitSigning</c>
/// (<c>sha256=</c> + lowercase-hex HMAC-SHA256), so batches the SDK signs verify at the
/// ingest edge. This is the recipe the <c>sdk-protocol</c> signed batches pin.</summary>
public sealed class IngestSignerTests
{
    [Fact]
    public void Sign_IsSha256PrefixedLowercaseHexHmac()
    {
        byte[] key = Encoding.UTF8.GetBytes("tsp_ing_abc123");
        byte[] payload = Encoding.UTF8.GetBytes("""{"batch_id":"x"}""");

        string signature = IngestSigner.Sign(key, payload);

        string expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(key, payload));
        Assert.Equal(expected, signature);
        Assert.StartsWith("sha256=", signature, StringComparison.Ordinal);
    }

    [Fact]
    public void Sign_IsDeterministic_ForSameKeyAndPayload()
    {
        byte[] key = Encoding.UTF8.GetBytes("tsp_ing_abc123");
        byte[] payload = [1, 2, 3, 4, 5];

        Assert.Equal(IngestSigner.Sign(key, payload), IngestSigner.Sign(key, payload));
    }

    [Fact]
    public void Sign_DiffersForDifferentKey()
    {
        byte[] payload = Encoding.UTF8.GetBytes("payload");

        string a = IngestSigner.Sign(Encoding.UTF8.GetBytes("key-a"), payload);
        string b = IngestSigner.Sign(Encoding.UTF8.GetBytes("key-b"), payload);

        Assert.NotEqual(a, b);
    }
}
