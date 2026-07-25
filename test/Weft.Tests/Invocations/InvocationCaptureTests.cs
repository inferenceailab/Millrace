using System.Text.Json;
using Weft.Invocations;
using Xunit;

namespace Weft.Tests.Invocations;

public sealed record OrderDto(string Number, decimal Amount);

public interface ICaptureProbe
{
    Task PlainAsync();

    ValueTask PlainValueTaskAsync();

    Task WithArgsAsync(int id, string name, OrderDto order);

    Task WithTokenAsync(int id, CancellationToken ct);

    Task GenericAsync<T>(T value);

    Task OverloadedAsync(int value);

    Task OverloadedAsync(string value);
}

public class InvocationCaptureTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    [Fact]
    public void Captures_declared_type_method_and_serialized_args()
    {
        var order = new OrderDto("A-42", 99.5m);
        var closureId = 7;

        var invocation = InvocationCapture.Capture<ICaptureProbe>(
            p => p.WithArgsAsync(closureId, "hello", order), Json);

        Assert.StartsWith("Weft.Tests.Invocations.ICaptureProbe, Weft.Tests", invocation.TypeName);
        Assert.Equal("WithArgsAsync", invocation.MethodName);
        Assert.Equal(3, invocation.ParameterTypes.Count);
        Assert.Equal("7", invocation.ArgumentsJson[0]);
        Assert.Equal("\"hello\"", invocation.ArgumentsJson[1]);
        Assert.Contains("A-42", invocation.ArgumentsJson[2]);
    }

    [Fact]
    public void Captures_computed_argument_expressions()
    {
        var values = new[] { 1, 2, 3 };

        var invocation = InvocationCapture.Capture<ICaptureProbe>(
            p => p.WithArgsAsync(values.Length + 10, string.Concat("a", "-", "b"), new OrderDto("N", 1m)), Json);

        Assert.Equal("13", invocation.ArgumentsJson[0]);
        Assert.Equal("\"a-b\"", invocation.ArgumentsJson[1]);
    }

    [Fact]
    public void Cancellation_token_serializes_as_null_placeholder()
    {
        var invocation = InvocationCapture.Capture<ICaptureProbe>(
            p => p.WithTokenAsync(5, CancellationToken.None), Json);

        Assert.Equal("5", invocation.ArgumentsJson[0]);
        Assert.Null(invocation.ArgumentsJson[1]);
    }

    [Fact]
    public void Distinguishes_overloads_by_parameter_types()
    {
        var byInt = InvocationCapture.Capture<ICaptureProbe>(p => p.OverloadedAsync(1), Json);
        var byString = InvocationCapture.Capture<ICaptureProbe>(p => p.OverloadedAsync("x"), Json);

        Assert.NotEqual(byInt.ParameterTypes[0], byString.ParameterTypes[0]);
    }

    [Fact]
    public void Rejects_generic_methods()
    {
        var e = Assert.Throws<NotSupportedException>(() =>
            InvocationCapture.Capture<ICaptureProbe>(p => p.GenericAsync(5), Json));

        Assert.Contains("Generic job methods", e.Message);
    }

    [Fact]
    public void Rejects_non_method_call_bodies()
    {
        Assert.Throws<ArgumentException>(() =>
            InvocationCapture.Capture<ICaptureProbe>(p => Task.CompletedTask, Json));
    }

    [Fact]
    public void Rejects_calls_not_on_the_service_parameter()
    {
        var e = Assert.Throws<ArgumentException>(() =>
            InvocationCapture.Capture<ICaptureProbe>(p => Task.Delay(1), Json));

        Assert.Contains("must return Task in 0.1", e.Message);
    }

    [Fact]
    public void Rejects_value_task_methods_wrapped_with_as_task()
    {
        var e = Assert.Throws<NotSupportedException>(() =>
            InvocationCapture.Capture<ICaptureProbe>(p => p.PlainValueTaskAsync().AsTask(), Json));

        Assert.Contains("ValueTask", e.Message);
        Assert.Contains("0.1", e.Message);
    }

    [Fact]
    public void Rejects_arguments_that_reference_the_service_parameter()
    {
        var e = Assert.Throws<ArgumentException>(() =>
            InvocationCapture.Capture<ICaptureProbe>(p => p.OverloadedAsync(p.GetHashCode()), Json));

        Assert.Contains("service parameter", e.Message);
    }
}
