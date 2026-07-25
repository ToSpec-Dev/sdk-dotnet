using ToSpec.Sdk.Tests.Support;

namespace ToSpec.Sdk.Tests;

/// <summary>The config poll is the cheap steady-state path (conditional GET → 304) and the
/// change-propagation path (a new version, or the kill switch, lands within one interval).
/// A poll that returns an unknown ruleset schema is refused and the last-good snapshot is
/// kept — the SDK never applies rules it cannot fully honor.</summary>
public sealed class ConfigPollTests
{
    private const string V1Yaml =
        """
        body:
          - { path: "$.x", action: drop }
        """;

    private const string V2Yaml =
        """
        body:
          - { path: "$.y", action: hash }
        """;

    [Fact]
    public async Task SteadyState_SendsIfNoneMatch_AndReceives304()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        var config = new FakeConfig { RulesetVersion = 1, CompiledJson = Rulesets.CompiledJson(V1Yaml) };
        var handler = new RecordingHandler(Responders.Standard(config));

        await using var harness = await ConformanceHarness.StartAsync(_ => { }, handler, cancellationToken: ct);

        await harness.WaitUntilAsync(
            () => harness.State.Current.RulesetVersion == 1,
            TimeSpan.FromSeconds(5),
            "the v1 ruleset to load");

        await harness.WaitUntilAsync(
            () => harness.Metrics.Snapshot().ConfigNotModified > 0,
            TimeSpan.FromSeconds(5),
            "a 304 on a subsequent conditional poll");

        Assert.Contains(
            handler.Requests,
            r => r.IsConfigGet && r.Headers.ContainsKey("If-None-Match"));
    }

    [Fact]
    public async Task PublishingNewVersion_PropagatesWithinOnePoll()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        var config = new FakeConfig { RulesetVersion = 1, CompiledJson = Rulesets.CompiledJson(V1Yaml) };
        var handler = new RecordingHandler(Responders.Standard(config));

        await using var harness = await ConformanceHarness.StartAsync(_ => { }, handler, cancellationToken: ct);

        await harness.WaitUntilAsync(
            () => harness.State.Current.RulesetVersion == 1,
            TimeSpan.FromSeconds(5),
            "the v1 ruleset to load");

        config.RulesetVersion = 2;
        config.CompiledJson = Rulesets.CompiledJson(V2Yaml);
        config.Bump();

        await harness.WaitUntilAsync(
            () => harness.State.Current.RulesetVersion == 2,
            TimeSpan.FromSeconds(5),
            "the v2 ruleset to propagate");

        Assert.Equal(2, harness.State.Current.RulesetVersion);
    }

    [Fact]
    public async Task UnknownRulesetSchema_IsRefused_LastGoodSnapshotKept()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        var config = new FakeConfig { RulesetVersion = 1, CompiledJson = Rulesets.CompiledJson(V1Yaml) };
        var handler = new RecordingHandler(Responders.Standard(config));

        await using var harness = await ConformanceHarness.StartAsync(_ => { }, handler, cancellationToken: ct);

        await harness.WaitUntilAsync(
            () => harness.State.Current.RulesetVersion == 1,
            TimeSpan.FromSeconds(5),
            "the v1 ruleset to load");

        // Serve a compiled ruleset with a schema the reader does not support.
        config.RulesetVersion = 2;
        config.CompiledJson = """{"schema":999,"trie":{}}""";
        config.KillSwitch = true;
        config.SamplingJson = """{"errors":50}""";
        config.Bump();

        await harness.WaitUntilAsync(
            () => harness.State.Current.KillSwitch,
            TimeSpan.FromSeconds(5),
            "the kill switch to propagate despite the unsupported ruleset");

        // Only the bad ruleset is refused; independent safety controls still move.
        Assert.Equal(1, harness.State.Current.RulesetVersion);
        Assert.NotNull(harness.State.Current.Ruleset);
        Assert.Equal(50, harness.State.Current.Sampling.ErrorPercent);
        Assert.Equal(config.ETag, harness.State.Current.ETag);
    }
}
