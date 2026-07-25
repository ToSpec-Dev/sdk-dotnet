using System.Net;
using System.Net.Http;
using System.Text;

namespace ToSpec.Sdk.Tests.Support;

/// <summary>Ready-made responder behaviours for <see cref="RecordingHandler"/>.</summary>
internal static class Responders
{
    /// <summary>Serves config from <paramref name="config"/> and accepts ingest POSTs
    /// (optionally overriding what the ingest endpoint does).</summary>
    public static Func<CapturedRequest, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Standard(
        FakeConfig config,
        Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>>? onIngest = null)
    {
        Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>> ingest =
            onIngest ?? ((_, _) => Task.FromResult(Accepted()));

        return async (captured, request, ct) =>
        {
            if (captured.IsConfigGet)
            {
                return config.Respond(request);
            }

            if (captured.IsIngestPost)
            {
                return await ingest(captured, ct);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    /// <summary>A 200 the way the real ingest edge answers an accepted batch.</summary>
    public static HttpResponseMessage Accepted() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            """{"batch_id":"00000000-0000-0000-0000-000000000001","ingested":1,"replayed":false}""",
            Encoding.UTF8,
            "application/json"),
    };

    /// <summary>Never completes until the SDK's own timeout cancels it — a dead endpoint.</summary>
    public static async Task<HttpResponseMessage> HangAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK); // unreachable
    }
}
