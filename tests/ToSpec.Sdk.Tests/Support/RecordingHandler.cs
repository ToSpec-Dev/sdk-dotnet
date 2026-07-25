using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http;

namespace ToSpec.Sdk.Tests.Support;

/// <summary>One outbound HTTP call the SDK made, captured at the wire boundary — the body
/// bytes are exactly what left the process (post-gzip when the SDK gzipped them).</summary>
internal sealed record CapturedRequest(
    HttpMethod Method,
    Uri Uri,
    IReadOnlyDictionary<string, string> Headers,
    string? ContentEncoding,
    byte[]? Body)
{
    public bool IsIngestPost => Method == HttpMethod.Post && Uri.AbsolutePath == ToSpecConformance.IngestPath;

    public bool IsConfigGet => Method == HttpMethod.Get && Uri.AbsolutePath == ToSpecConformance.ConfigPath;

    /// <summary>The batch body as UTF-8 text, gunzipping first when it was gzip-encoded —
    /// i.e. the actual bytes the ingest server would parse.</summary>
    public string BodyText()
    {
        if (Body is null)
        {
            return "";
        }

        byte[] bytes = string.Equals(ContentEncoding, "gzip", StringComparison.OrdinalIgnoreCase)
            ? Gunzip(Body)
            : Body;
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static byte[] Gunzip(byte[] gzipped)
    {
        using var input = new MemoryStream(gzipped);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}

/// <summary>
/// A stub <see cref="HttpMessageHandler"/> that records every request the SDK sends (so a
/// test can scan the real outbound wire bytes) and defers the response to a supplied
/// responder — which may return a canned response, a 304, or hang forever to simulate a
/// dead ingest endpoint. The request body is fully read <b>before</b> the responder runs,
/// so captures are available even when the responder hangs.
/// </summary>
internal sealed class RecordingHandler(
    Func<CapturedRequest, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    : HttpMessageHandler
{
    public ConcurrentQueue<CapturedRequest> Requests { get; } = new();

    public int IngestPostCount => Requests.Count(r => r.IsIngestPost);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[]? body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        string? encoding = request.Content?.Headers.ContentEncoding.FirstOrDefault();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, IEnumerable<string> values) in request.Headers)
        {
            headers[key] = string.Join(",", values);
        }

        var captured = new CapturedRequest(request.Method, request.RequestUri!, headers, encoding, body);
        Requests.Enqueue(captured);

        return await responder(captured, request, cancellationToken);
    }
}
