using System.Text;
using System.Text.Json;

namespace ToSpec.Sdk.Tests.Support;

/// <summary>
/// Builds a scannable view of an outbound batch that <b>decodes the base64 bodies</b>.
/// This matters: bodies ride the wire base64-encoded, so grepping the raw gzipped text for
/// a planted PAN would pass even if the PAN were present (its base64 form differs). A
/// correct structural scan decodes each <c>req_body</c>/<c>resp_body</c> and inspects the
/// bytes that would actually be reconstructed server-side.
/// </summary>
internal static class WireInspector
{
    /// <summary>The full batch JSON (headers, metadata) plus every body decoded from base64
    /// — the complete set of bytes the ingest server could reconstruct from this batch.</summary>
    public static string ScannableText(CapturedRequest post)
    {
        string wire = post.BodyText();
        var sb = new StringBuilder(wire);

        using var doc = JsonDocument.Parse(wire);
        if (doc.RootElement.TryGetProperty("events", out JsonElement events)
            && events.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement ev in events.EnumerateArray())
            {
                AppendDecodedBody(sb, ev, "req_body");
                AppendDecodedBody(sb, ev, "resp_body");
            }
        }

        return sb.ToString();
    }

    private static void AppendDecodedBody(StringBuilder sb, JsonElement ev, string name)
    {
        if (ev.TryGetProperty(name, out JsonElement body) && body.ValueKind == JsonValueKind.String)
        {
            byte[] bytes = Convert.FromBase64String(body.GetString()!);
            sb.Append('\n').Append(Encoding.UTF8.GetString(bytes));
        }
    }
}
