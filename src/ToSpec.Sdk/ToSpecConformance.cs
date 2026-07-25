namespace ToSpec.Sdk;

/// <summary>Shared constants for the ToSpec conformance SDK.</summary>
public static class ToSpecConformance
{
    /// <summary>Named <see cref="System.Net.Http.HttpClient"/> the batcher and config
    /// poller resolve. Registered by <c>AddToSpecConformance</c>; tests substitute the
    /// primary handler by this name.</summary>
    public const string HttpClientName = "tospec-conformance";

    /// <summary>Relative path of the ingest edge (POST).</summary>
    public const string IngestPath = "/v1/ingest";

    /// <summary>Relative path of the SDK config endpoint (GET).</summary>
    public const string ConfigPath = "/v1/sdk/config";
}
