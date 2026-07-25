using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Millrace.Storage;

namespace Millrace.Invocations;

/// <summary>
/// Captures a job call expression into the serialized <see cref="JobInvocation"/> wire format
/// (ARCHITECTURE.md §5.2): declared service type + method + per-argument JSON. Argument
/// sub-expressions are evaluated at capture time; guidance is to keep job signatures stable and
/// pass ids, not entities.
/// </summary>
public static class InvocationCapture
{
    public static JobInvocation Capture<T>(Expression<Func<T, Task>> call, JsonSerializerOptions json)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(call);

        if (call.Body is not MethodCallExpression method)
        {
            throw new ArgumentException(
                "Job expressions must be a single method call on the service parameter, " +
                "e.g. s => s.SendAsync(orderId).", nameof(call));
        }

        if (method.Method.Name == nameof(ValueTask.AsTask)
            && method.Object is { } receiver
            && (receiver.Type == typeof(ValueTask)
                || (receiver.Type.IsGenericType && receiver.Type.GetGenericTypeDefinition() == typeof(ValueTask<>))))
        {
            throw new NotSupportedException(
                "Job methods must return Task in 0.1; ValueTask-returning methods are not " +
                "supported — wrap the call in a Task-returning method instead of .AsTask().");
        }

        if (method.Object is not ParameterExpression parameter || parameter != call.Parameters[0])
        {
            throw new ArgumentException(
                "Job expressions must invoke an instance method directly on the service " +
                "parameter (static methods, extension methods, and wrappers such as " +
                "ValueTask.AsTask() are not supported; job methods must return Task in 0.1).",
                nameof(call));
        }

        if (method.Method.IsGenericMethod)
        {
            throw new NotSupportedException(
                "Generic job methods are not supported in 0.1; wrap the call in a non-generic method.");
        }

        var parameters = method.Method.GetParameters();
        var parameterTypes = new string[parameters.Length];
        var argumentsJson = new string?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameterType = parameters[i].ParameterType;
            if (parameterType.IsByRef)
            {
                throw new NotSupportedException(
                    $"Job method '{method.Method.Name}' has a ref/in/out parameter " +
                    $"'{parameters[i].Name}', which cannot be serialized.");
            }

            parameterTypes[i] = TypeNameFormatter.Format(parameterType);

            if (parameterType == typeof(CancellationToken))
            {
                // Placeholder — the worker injects the job's execution token at invoke time.
                argumentsJson[i] = null;
                continue;
            }

            var argument = method.Arguments[i];
            if (ReferencesParameter(argument, call.Parameters[0]))
            {
                throw new ArgumentException(
                    $"Argument {i} of '{method.Method.Name}' references the service parameter " +
                    "— job arguments are evaluated and serialized at enqueue time, so they " +
                    "cannot depend on the service instance.", nameof(call));
            }

            var value = Evaluate(argument);
            argumentsJson[i] = JsonSerializer.Serialize(value, parameterType, json);
        }

        return new JobInvocation
        {
            TypeName = TypeNameFormatter.Format(typeof(T)),
            MethodName = method.Method.Name,
            ParameterTypes = parameterTypes,
            ArgumentsJson = argumentsJson,
        };
    }

    private static bool ReferencesParameter(Expression expression, ParameterExpression parameter)
    {
        var detector = new ParameterDetector(parameter);
        detector.Visit(expression);
        return detector.Found;
    }

    private sealed class ParameterDetector(ParameterExpression parameter) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == parameter)
            {
                Found = true;
            }

            return base.VisitParameter(node);
        }
    }

    private static object? Evaluate(Expression expression)
    {
        if (expression is ConstantExpression constant)
        {
            return constant.Value;
        }

        // Closure captures, member accesses, computed values: evaluate the sub-expression.
        var lambda = Expression.Lambda<Func<object?>>(Expression.Convert(expression, typeof(object)));
        return lambda.Compile(preferInterpretation: true).Invoke();
    }
}
