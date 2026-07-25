using Xunit;

namespace Millrace.Storage.PostgreSql.Tests;

/// <summary>
/// Guards the rule that decides whether an unreachable PostgreSQL skips the conformance suite or
/// fails it. Getting this wrong is silent: the suite reports "Test Run Successful" for a run that
/// verified nothing, which is exactly what these tests exist to prevent regressing.
/// </summary>
public sealed class PostgresRequirementPolicyTests
{
    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("  true  ")]
    public void Explicit_truthy_flag_requires_postgres(string flag)
        => Assert.True(PostgresTestDatabase.ResolveRequired(flag, ciFlag: null));

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("anything else")]
    public void Explicit_non_truthy_flag_allows_skipping(string flag)
        => Assert.False(PostgresTestDatabase.ResolveRequired(flag, ciFlag: null));

    [Fact]
    public void Ci_alone_requires_postgres()
        => Assert.True(PostgresTestDatabase.ResolveRequired(explicitFlag: null, ciFlag: "true"));

    [Fact]
    public void A_new_ci_job_that_sets_nothing_is_strict_by_default()
    {
        // The point of defaulting to strict: a job added later inherits the requirement instead of
        // silently skipping the whole suite and still reporting success.
        Assert.True(PostgresTestDatabase.ResolveRequired(explicitFlag: null, ciFlag: "1"));
        Assert.True(PostgresTestDatabase.ResolveRequired(explicitFlag: "", ciFlag: "true"));
        Assert.True(PostgresTestDatabase.ResolveRequired(explicitFlag: "   ", ciFlag: "true"));
    }

    [Fact]
    public void Explicit_opt_out_beats_ci()
    {
        // This is the Windows CI job's contract: CI is set, but Windows runners cannot run Linux
        // containers, so the job opts out deliberately.
        Assert.False(PostgresTestDatabase.ResolveRequired(explicitFlag: "false", ciFlag: "true"));
    }

    [Fact]
    public void A_developer_machine_outside_ci_may_skip()
        => Assert.False(PostgresTestDatabase.ResolveRequired(explicitFlag: null, ciFlag: null));
}
