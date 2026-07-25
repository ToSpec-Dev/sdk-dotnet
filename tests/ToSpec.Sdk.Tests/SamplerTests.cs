using System.Text.Json;

namespace ToSpec.Sdk.Tests;

/// <summary>Sampling honors independent error/success percentages (SPEC §9 "100% errors,
/// 5% success") and reads them from the config's <c>sampling_rules</c> jsonb.</summary>
public sealed class SamplerTests
{
    [Fact]
    public void ErrorsAlways_SuccessNever_EmitsErrorsDropsSuccess()
    {
        var sampler = new Sampler(() => 50);
        var rule = new SamplingRule(100, 0);

        Assert.True(sampler.ShouldEmit(rule, 500));
        Assert.False(sampler.ShouldEmit(rule, 200));
    }

    [Fact]
    public void SuccessPercent_ThresholdIsStrictLessThan()
    {
        var rule = new SamplingRule(100, 5);

        Assert.True(new Sampler(() => 4).ShouldEmit(rule, 200));
        Assert.False(new Sampler(() => 5).ShouldEmit(rule, 200));
    }

    [Fact]
    public void ErrorBoundary_400IsError_399IsSuccess()
    {
        var rule = new SamplingRule(100, 0);

        Assert.True(new Sampler(() => 99).ShouldEmit(rule, 400));
        Assert.False(new Sampler(() => 99).ShouldEmit(rule, 399));
    }

    [Fact]
    public void Parse_ReadsErrorsAndSuccessPercents()
    {
        using var doc = JsonDocument.Parse("""{"errors":100,"success":5}""");
        SamplingRule rule = SamplingRule.Parse(doc.RootElement);

        Assert.Equal(100, rule.ErrorPercent);
        Assert.Equal(5, rule.SuccessPercent);
    }

    [Fact]
    public void Parse_MissingOrNull_DefaultsToCaptureAll()
    {
        Assert.Equal(SamplingRule.CaptureAll, SamplingRule.Parse(null));

        using var empty = JsonDocument.Parse("{}");
        Assert.Equal(SamplingRule.CaptureAll, SamplingRule.Parse(empty.RootElement));
    }
}
