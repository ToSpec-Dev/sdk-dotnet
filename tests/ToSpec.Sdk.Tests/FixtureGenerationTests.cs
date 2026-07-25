using ToSpec.Sdk.Tests.Fixtures;

namespace ToSpec.Sdk.Tests;

/// <summary>
/// Keeps the committed <c>ToSpec-Dev/sdk-protocol</c> golden files in lockstep with the
/// generator. Normal runs assert the committed files (copied to the test output) equal what
/// the SDK produces — so any redaction/serialization change that would move the wire format
/// fails here until the fixtures are regenerated. Regenerate by running this suite with
/// <c>TOSPEC_WRITE_FIXTURES=&lt;dir1&gt;;&lt;dir2&gt;</c> set to the target fixtures directories.
/// </summary>
public sealed class FixtureGenerationTests
{
    [Fact]
    public void WriteGoldenFiles_WhenRequested()
    {
        string? targets = Environment.GetEnvironmentVariable("TOSPEC_WRITE_FIXTURES");
        if (string.IsNullOrWhiteSpace(targets))
        {
            return; // no-op in normal/CI runs
        }

        FixtureSet set = FixtureFactory.BuildAll();
        foreach (string dir in targets.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            FixtureWriter.Write(dir, set);
        }
    }

    [Fact]
    public void CommittedFixtures_AreUpToDate()
    {
        string baseDir = Path.Combine(AppContext.BaseDirectory, "fixtures");
        Assert.True(Directory.Exists(baseDir), $"fixtures were not copied to output at {baseDir}");

        FixtureSet set = FixtureFactory.BuildAll();
        foreach ((string path, string content) in FixtureWriter.Render(set))
        {
            string full = Path.Combine(baseDir, path);
            Assert.True(File.Exists(full), $"missing committed fixture: {path} (run with TOSPEC_WRITE_FIXTURES set)");
            string committed = File.ReadAllText(full).ReplaceLineEndings("\n");
            Assert.Equal(content, committed);
        }
    }
}
