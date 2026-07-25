using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Millrace.Invocations;
using Millrace.Storage;
using Millrace.Tenancy;
using Xunit;

namespace Millrace.Tests.Invocations;

public interface IBaseProbe
{
    Task BaseCallAsync(string tag);
}

public interface IDerivedProbe : IBaseProbe
{
    Task DerivedCallAsync(string tag);
}

public sealed class ExecutionLog
{
    public List<string> Calls { get; } = [];

    public string? ObservedTenant { get; set; }

    public bool TokenWasCancellable { get; set; }
}

/// <summary>Implements the derived interface explicitly — resolution must still find methods.</summary>
public sealed class ExplicitProbe(ExecutionLog log, ITenantContextAccessor tenants) : IDerivedProbe
{
    Task IBaseProbe.BaseCallAsync(string tag)
    {
        log.Calls.Add($"base:{tag}");
        log.ObservedTenant = tenants.TenantId;
        return Task.CompletedTask;
    }

    Task IDerivedProbe.DerivedCallAsync(string tag)
    {
        log.Calls.Add($"derived:{tag}");
        return Task.CompletedTask;
    }
}

public interface ITokenProbe
{
    Task RunAsync(int id, CancellationToken ct);

    Task ThrowAsync();
}

public interface IScopedProbe
{
    Task TouchAsync();
}

/// <summary>Scoped registration: each execution must see a fresh instance.</summary>
public sealed class ScopedProbe(ExecutionLog log) : IScopedProbe
{
    private readonly Guid _instanceId = Guid.NewGuid();

    public Task TouchAsync()
    {
        log.Calls.Add($"scoped:{_instanceId}");
        return Task.CompletedTask;
    }
}

public sealed class TokenProbe(ExecutionLog log) : ITokenProbe
{
    public Task RunAsync(int id, CancellationToken ct)
    {
        log.Calls.Add($"run:{id}");
        log.TokenWasCancellable = ct.CanBeCanceled;
        return Task.CompletedTask;
    }

    public Task ThrowAsync() => throw new InvalidOperationException("probe failure");
}

public class JobExecutorTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    private static (JobExecutor Executor, ExecutionLog Log, ServiceProvider Provider) Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ExecutionLog>();
        services.AddSingleton<ITenantContextAccessor, AmbientTenantContextAccessor>();
        services.AddTransient<IDerivedProbe, ExplicitProbe>();
        services.AddTransient<ITokenProbe, TokenProbe>();
        services.AddScoped<IScopedProbe, ScopedProbe>();
        var provider = services.BuildServiceProvider();

        var executor = new JobExecutor(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ITenantContextAccessor>(),
            Options.Create(new MillraceOptions()));
        return (executor, provider.GetRequiredService<ExecutionLog>(), provider);
    }

    private static JobRecord Record(JobInvocation invocation, string? tenantId = null) => new()
    {
        Id = JobId.New(),
        Queue = "default",
        Invocation = invocation,
        State = JobState.Processing,
        CreatedAt = DateTimeOffset.UtcNow,
        Retry = Retry.None,
        TenantId = tenantId,
    };

    [Fact]
    public async Task Executes_and_injects_the_execution_token()
    {
        var (executor, log, provider) = Build();
        await using var _ = provider;
        var invocation = InvocationCapture.Capture<ITokenProbe>(
            p => p.RunAsync(42, CancellationToken.None), Json);

        using var cts = new CancellationTokenSource();
        await executor.ExecuteAsync(Record(invocation), cts.Token);

        Assert.Equal(new[] { "run:42" }, log.Calls);
        Assert.True(log.TokenWasCancellable, "the placeholder token must be replaced by the job's token");
    }

    [Fact]
    public async Task Restores_the_jobs_tenant_into_the_execution_scope()
    {
        var (executor, log, provider) = Build();
        await using var _ = provider;
        var invocation = InvocationCapture.Capture<IDerivedProbe>(p => p.BaseCallAsync("t"), Json);

        await executor.ExecuteAsync(Record(invocation, tenantId: "tenant-9"), CancellationToken.None);

        Assert.Equal("tenant-9", log.ObservedTenant);
        // And the ambient value is restored after execution.
        Assert.Null(provider.GetRequiredService<ITenantContextAccessor>().TenantId);
    }

    [Fact]
    public async Task Resolves_base_interface_methods_against_explicit_implementations()
    {
        var (executor, log, provider) = Build();
        await using var _ = provider;

        // Captured through the derived interface, declared on the base, implemented explicitly.
        var baseCall = InvocationCapture.Capture<IDerivedProbe>(p => p.BaseCallAsync("x"), Json);
        var derivedCall = InvocationCapture.Capture<IDerivedProbe>(p => p.DerivedCallAsync("y"), Json);

        await executor.ExecuteAsync(Record(baseCall), CancellationToken.None);
        await executor.ExecuteAsync(Record(derivedCall), CancellationToken.None);

        Assert.Equal(new[] { "base:x", "derived:y" }, log.Calls);
    }

    [Fact]
    public async Task Each_execution_gets_a_fresh_di_scope()
    {
        var (executor, log, provider) = Build();
        await using var _ = provider;
        var invocation = InvocationCapture.Capture<IScopedProbe>(p => p.TouchAsync(), Json);

        await executor.ExecuteAsync(Record(invocation), CancellationToken.None);
        await executor.ExecuteAsync(Record(invocation), CancellationToken.None);

        Assert.Equal(2, log.Calls.Count);
        Assert.NotEqual(log.Calls[0], log.Calls[1]);
    }

    [Fact]
    public async Task Tenant_is_restored_even_when_the_job_throws()
    {
        var (executor, _, provider) = Build();
        await using var __ = provider;
        var tenants = provider.GetRequiredService<ITenantContextAccessor>();
        var invocation = InvocationCapture.Capture<ITokenProbe>(p => p.ThrowAsync(), Json);

        using var outer = tenants.BeginScope("outer");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.ExecuteAsync(Record(invocation, tenantId: "inner"), CancellationToken.None));

        // The failure path must restore the ambient tenant just like the success path.
        Assert.Equal("outer", tenants.TenantId);
    }

    [Fact]
    public async Task Job_exceptions_surface_unwrapped()
    {
        var (executor, _, provider) = Build();
        await using var _ = provider;
        var invocation = InvocationCapture.Capture<ITokenProbe>(p => p.ThrowAsync(), Json);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.ExecuteAsync(Record(invocation), CancellationToken.None));
        Assert.Equal("probe failure", e.Message);
    }

    [Fact]
    public async Task Missing_method_reports_the_breaking_deploy_rule()
    {
        var (executor, _, provider) = Build();
        await using var _ = provider;
        var invocation = InvocationCapture.Capture<ITokenProbe>(p => p.ThrowAsync(), Json)
            with { MethodName = "RenamedAsync" };

        var e = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.ExecuteAsync(Record(invocation), CancellationToken.None));
        Assert.Contains("breaking deploy", e.Message);
    }
}
