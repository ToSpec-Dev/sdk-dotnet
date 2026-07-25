using Microsoft.AspNetCore.Http;
using System.Text.Json;
using ToSpec.Redact;
using ToSpec.Sdk.Tests.Support;

namespace ToSpec.Sdk.Tests;

public sealed class ExchangeRedactorTests
{
    [Fact]
    public void MixedStructuredFormats_RetainsRequestAndDropsResponse()
    {
        using JsonDocument edge = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "edge-cases.json")));
        JsonElement mixed = edge.RootElement.GetProperty("mixed_formats");
        Assert.Equal("retain_request_drop_response", mixed.GetProperty("policy").GetString());
        var snapshot = new ConformanceSnapshot
        {
            Ruleset = Rulesets.Compile("body:\n  - { path: \"$.secret\", action: drop }"),
            RulesetVersion = 1,
        };
        var exchange = new CapturedExchange
        {
            EventId = Guid.NewGuid(),
            PartnerId = Guid.NewGuid(),
            Ts = DateTimeOffset.UtcNow,
            Method = "POST",
            Path = "/mixed",
            Status = 200,
            LatencyMs = 1,
            RequestHeaders = new HeaderDictionary(),
            ResponseHeaders = new HeaderDictionary(),
            RequestBody = "{\"ok\":true}"u8.ToArray(),
            ResponseBody = "<ok>true</ok>"u8.ToArray(),
            RequestContentType = "application/json",
            ResponseContentType = "application/xml",
            RequestSize = 11,
            ResponseSize = 13,
        };

        var result = ExchangeRedactor.Redact(
            exchange, snapshot, new RedactionKeyring(new byte[32], 1), new ConformanceMetrics());

        Assert.NotNull(result.ReqBody);
        Assert.Null(result.RespBody);
        Assert.Equal(mixed.GetProperty("request").GetString(), result.ContentFormat);
    }
}
