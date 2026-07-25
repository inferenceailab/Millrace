using Xunit;

namespace Millrace.Tests;

public class JobIdTests
{
    [Fact]
    public void New_ids_are_unique_and_time_ordered()
    {
        var first = JobId.New();
        var second = JobId.New();

        Assert.NotEqual(first, second);
        Assert.Equal(7, first.Value.Version);
    }
}
