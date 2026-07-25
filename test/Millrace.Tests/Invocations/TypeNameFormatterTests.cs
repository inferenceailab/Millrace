using Millrace.Invocations;
using Xunit;

namespace Millrace.Tests.Invocations;

public sealed record FormatterProbe(int Value);

public class TypeNameFormatterTests
{
    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(int))]
    [InlineData(typeof(FormatterProbe))]
    [InlineData(typeof(List<FormatterProbe>))]
    [InlineData(typeof(Dictionary<int, FormatterProbe>))]
    [InlineData(typeof(FormatterProbe[]))]
    [InlineData(typeof(List<FormatterProbe>[]))]
    [InlineData(typeof(int?))]
    [InlineData(typeof(List<Dictionary<string, List<FormatterProbe?[]>>>))]
    [InlineData(typeof(int[,]))]
    public void Format_is_version_free_and_resolves_back(Type type)
    {
        var name = TypeNameFormatter.Format(type);

        Assert.DoesNotContain("Version=", name);
        Assert.DoesNotContain("Culture=", name);
        Assert.DoesNotContain("PublicKeyToken=", name);
        Assert.Same(type, TypeNameFormatter.Resolve(name));
    }

    [Fact]
    public void Format_uses_simple_assembly_names_at_every_level()
    {
        var name = TypeNameFormatter.Format(typeof(Dictionary<int, FormatterProbe>));

        Assert.Contains("Millrace.Tests", name);           // the generic argument's assembly
        Assert.Contains("System.Private.CoreLib", name); // the definition's assembly
    }

    [Fact]
    public void Resolve_failure_mentions_the_breaking_deploy_rule()
    {
        var e = Assert.Throws<InvalidOperationException>(() =>
            TypeNameFormatter.Resolve("No.Such.Type, No.Such.Assembly"));

        Assert.Contains("breaking deploy", e.Message);
    }
}
