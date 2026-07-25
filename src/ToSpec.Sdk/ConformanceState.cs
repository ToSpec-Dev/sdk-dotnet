using ToSpec.Redact;

namespace ToSpec.Sdk;

/// <summary>
/// The immutable configuration the SDK acts on, swapped atomically by the config poller.
/// A body is only ever transmitted after <see cref="Ruleset"/> has been applied to it, so
/// before the first successful poll (<see cref="Ruleset"/> is null) the SDK captures
/// metadata only and never a body — redaction-before-transmission holds even during
/// startup.
/// </summary>
public sealed record ConformanceSnapshot
{
    /// <summary>The compiled redaction ruleset; null until the first config poll lands a
    /// published ruleset (version 0 = none). Null ⇒ bodies are dropped, not transmitted.</summary>
    public CompiledRuleset? Ruleset { get; init; }

    public int RulesetVersion { get; init; }

    public SamplingRule Sampling { get; init; } = SamplingRule.CaptureAll;

    public bool KillSwitch { get; init; }

    /// <summary>The last served <c>ETag</c>, echoed as <c>If-None-Match</c> so steady-state
    /// polls are a cheap 304.</summary>
    public string? ETag { get; init; }

    /// <summary>The fail-safe starting point: no ruleset (⇒ metadata-only), capture all,
    /// not killed, no ETag.</summary>
    public static readonly ConformanceSnapshot Initial = new();
}

/// <summary>Holds the current <see cref="ConformanceSnapshot"/> behind a volatile
/// reference — writes from the single config poller are published atomically to the many
/// request threads that read <see cref="Current"/>.</summary>
public sealed class ConformanceState
{
    private volatile ConformanceSnapshot _current = ConformanceSnapshot.Initial;

    public ConformanceSnapshot Current => _current;

    public void Update(ConformanceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _current = snapshot;
    }
}
