namespace ToSpec.Sdk;

/// <summary>
/// Maps an HTTP <c>Content-Type</c> to the <c>content_format</c> vocabulary the ingest
/// envelope and the redaction engine share ("json" | "xml" | "text" | "binary"). Only
/// "json" and "xml" have structured redactors (<c>BodyRedactorRegistry</c>); "text" and
/// "binary" have none, so their bodies are dropped rather than transmitted unredacted.
/// </summary>
internal static class ContentFormat
{
    public const string Json = "json";
    public const string Xml = "xml";
    public const string Text = "text";
    public const string Binary = "binary";

    public static string Detect(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return Binary;
        }

        ReadOnlySpan<char> ct = contentType.AsSpan();
        int semicolon = ct.IndexOf(';');
        if (semicolon >= 0)
        {
            ct = ct[..semicolon];
        }

        ct = ct.Trim();

        // application/json, application/vnd.api+json, text/json, …
        if (ct.EndsWith("json", StringComparison.OrdinalIgnoreCase)
            || ct.Contains("+json", StringComparison.OrdinalIgnoreCase))
        {
            return Json;
        }

        if (ct.EndsWith("xml", StringComparison.OrdinalIgnoreCase)
            || ct.Contains("+xml", StringComparison.OrdinalIgnoreCase))
        {
            return Xml;
        }

        if (ct.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return Text;
        }

        return Binary;
    }
}
