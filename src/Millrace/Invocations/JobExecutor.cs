using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Millrace.Storage;
using Millrace.Tenancy;

namespace Millrace.Invocations;

/// <summary>
/// Executes a claimed job: resolves the declared service type from a fresh DI scope, restores
/// the job's tenant into the ambient context, deserializes arguments, and invokes the method
/// with the job's execution token injected at <see cref="CancellationToken"/> positions.
/// </summary>
public sealed class JobExecutor(
    IServiceScopeFactory scopes,
    ITenantContextAccessor tenants,
    IOptions<MillraceOptions> options)
{
    private readonly JsonSerializerOptions _json = options.Value.SerializerOptions;

    /// <summary>
    /// Runs the job and returns the effects it asked to have committed with its own completion.
    /// </summary>
    /// <remarks>
    /// The side-effect accumulator is resolved from the execution scope <em>after</em> the call, so
    /// a handler can describe a checkpoint or follow-on jobs without being able to commit anything
    /// itself — the worker folds them into the terminal transition.
    /// </remarks>
    public async Task<JobSideEffects> ExecuteAsync(JobRecord job, CancellationToken ct)
    {
        var serviceType = TypeNameFormatter.Resolve(job.Invocation.TypeName);
        var method = ResolveMethod(serviceType, job.Invocation);
        var parameters = method.GetParameters();

        var arguments = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].ParameterType == typeof(CancellationToken))
            {
                arguments[i] = ct;
                continue;
            }

            var argumentJson = job.Invocation.ArgumentsJson[i];
            arguments[i] = argumentJson is null
                ? null
                : JsonSerializer.Deserialize(argumentJson, parameters[i].ParameterType, _json);
        }

        await using var scope = scopes.CreateAsyncScope();
        using var tenantScope = tenants.BeginScope(job.TenantId);

        var target = scope.ServiceProvider.GetRequiredService(serviceType);
        var result = method.Invoke(target, BindingFlags.DoNotWrapExceptions, binder: null, arguments, culture: null);
        await ((Task)result!).ConfigureAwait(false);

        return scope.ServiceProvider.GetRequiredService<JobSideEffects>();
    }

    /// <summary>
    /// Resolves against the declared service type's methods, then (for interfaces) every
    /// inherited interface — so base-interface methods enqueued through a derived interface and
    /// explicit interface implementations both work. Resolution never consults the target's
    /// concrete type. Matching is by name plus element-wise equality of parameter types rendered
    /// through <see cref="TypeNameFormatter"/> — both sides normalized, raw names never compared.
    /// </summary>
    internal static MethodInfo ResolveMethod(Type serviceType, JobInvocation invocation)
    {
        IEnumerable<MethodInfo> candidates = serviceType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        if (serviceType.IsInterface)
        {
            candidates = candidates.Concat(serviceType.GetInterfaces()
                .SelectMany(i => i.GetMethods(BindingFlags.Public | BindingFlags.Instance)));
        }

        foreach (var candidate in candidates)
        {
            if (!string.Equals(candidate.Name, invocation.MethodName, StringComparison.Ordinal)
                || candidate.IsGenericMethod)
            {
                continue;
            }

            var parameters = candidate.GetParameters();
            if (parameters.Length != invocation.ParameterTypes.Count)
            {
                continue;
            }

            var matches = true;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (!string.Equals(
                        TypeNameFormatter.Format(parameters[i].ParameterType),
                        invocation.ParameterTypes[i],
                        StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Job method '{invocation.MethodName}({string.Join(", ", invocation.ParameterTypes)})' " +
            $"was not found on '{serviceType}'. Renaming methods used by in-flight jobs is a " +
            "breaking deploy.");
    }
}
