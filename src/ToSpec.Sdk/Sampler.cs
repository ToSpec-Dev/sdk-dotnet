namespace ToSpec.Sdk;

/// <summary>
/// Decides whether one exchange is emitted, given the sampling rule and the response
/// status. Errors (status ≥ 400) and successes are sampled independently — the common
/// production shape is "100% of errors, a few % of successes". The roll source is
/// injectable so tests are deterministic; in production it is <see cref="Random.Shared"/>.
/// </summary>
public sealed class Sampler
{
    private readonly Func<int> _roll;

    /// <summary>Production constructor: uniform roll in [0,100) from <see cref="Random.Shared"/>.</summary>
    public Sampler()
        : this(static () => Random.Shared.Next(100))
    {
    }

    /// <summary><paramref name="rollProvider"/> returns a value in [0,100); an event is
    /// emitted when the roll is below the applicable percentage.</summary>
    public Sampler(Func<int> rollProvider)
    {
        ArgumentNullException.ThrowIfNull(rollProvider);
        _roll = rollProvider;
    }

    public bool ShouldEmit(SamplingRule rule, int? status)
    {
        int percent = IsError(status) ? rule.ErrorPercent : rule.SuccessPercent;
        if (percent >= 100)
        {
            return true;
        }

        if (percent <= 0)
        {
            return false;
        }

        return _roll() < percent;
    }

    private static bool IsError(int? status) => status is >= 400;
}
