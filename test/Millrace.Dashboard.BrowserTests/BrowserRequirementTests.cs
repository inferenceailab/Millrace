using Xunit;

namespace Millrace.Dashboard.BrowserTests;

/// <summary>
/// The strictness policy itself, which decides whether a missing browser skips the run or fails it.
/// </summary>
/// <remarks>
/// Pure, and tested without a browser, for the same reason the PostgreSQL suite tests its own
/// policy: the whole value of this suite is that it cannot quietly report success for a run that
/// rendered nothing, and that promise lives in this one function.
/// </remarks>
public sealed class BrowserRequirementTests
{
    [Theory]
    [InlineData("true", null, true)]
    [InlineData("1", null, true)]
    [InlineData("yes", null, true)]
    [InlineData("false", "true", false)]
    [InlineData("0", "1", false)]
    public void An_explicit_flag_wins_over_CI(string? explicitFlag, string? ci, bool expected)
        => Assert.Equal(expected, BrowserRequirement.ResolveRequired(explicitFlag, ci));

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void CI_decides_when_no_explicit_flag_is_set(string? ci, bool expected)
        => Assert.Equal(expected, BrowserRequirement.ResolveRequired(null, ci));

    /// <summary>
    /// Every CI job is strict by default, so a new job cannot skip the suite and still pass.
    /// </summary>
    [Fact]
    public void A_CI_job_that_says_nothing_is_strict()
        => Assert.True(BrowserRequirement.ResolveRequired(explicitFlag: null, ciFlag: "true"));
}
