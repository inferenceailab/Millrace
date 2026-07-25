using Microsoft.Extensions.DependencyInjection;
using Millrace.Workflows;
using Xunit;

namespace Millrace.Tests.Workflows;

public sealed class WorkflowRegistryTests
{
    private sealed class V1 : IWorkflow<InvoiceData>
    {
        public string Id => "invoice-approval";
        public int Version => 1;
        public void Build(IWorkflowBuilder<InvoiceData> flow) => flow.StartWith<Validate>();
    }

    private sealed class V2 : IWorkflow<InvoiceData>
    {
        public string Id => "invoice-approval";
        public int Version => 2;
        public void Build(IWorkflowBuilder<InvoiceData> flow) => flow.StartWith<Validate>().Then<PostToErp>();
    }

    private sealed class NotAWorkflow
    {
    }

    [Fact]
    public void Both_versions_stay_resolvable_and_the_latest_is_the_default()
    {
        var registry = new WorkflowRegistry(
            [WorkflowDefinition.Compile(new V1()), WorkflowDefinition.Compile(new V2())]);

        // An in-flight instance pinned to v1 must still resolve after v2 ships — that is the whole
        // point of keeping old versions registered.
        Assert.True(registry.TryGet("invoice-approval", 1, out var v1));
        Assert.True(registry.TryGet("invoice-approval", 2, out var v2));
        Assert.Equal(1, v1.Version);
        Assert.Equal(2, v2.Version);
        Assert.Equal(2, registry.GetLatest("invoice-approval")?.Version);
    }

    [Fact]
    public void An_unknown_id_or_version_resolves_to_nothing()
    {
        var registry = new WorkflowRegistry([WorkflowDefinition.Compile(new V1())]);

        Assert.False(registry.TryGet("invoice-approval", 99, out _));
        Assert.False(registry.TryGet("nope", 1, out _));
        Assert.Null(registry.GetLatest("nope"));
    }

    [Fact]
    public void Registering_the_same_id_and_version_twice_is_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new WorkflowRegistry(
            [WorkflowDefinition.Compile(new V1()), WorkflowDefinition.Compile(new V1())]));

        Assert.Contains("registered twice", ex.Message);
    }

    [Fact]
    public void AddWorkflow_compiles_at_registration_so_a_bad_definition_fails_at_startup()
    {
        var services = new ServiceCollection();

        // Not when an instance first runs it, hours later, in production.
        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddMillrace(m => m.UseInMemoryStorage().AddWorkflow<EmptyWorkflow>()));

        Assert.Contains("no steps", ex.Message);
    }

    private sealed class EmptyWorkflow : IWorkflow<InvoiceData>
    {
        public string Id => "empty";
        public int Version => 1;
        public void Build(IWorkflowBuilder<InvoiceData> flow) { }
    }

    [Fact]
    public void AddWorkflow_rejects_a_type_that_is_not_a_workflow()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddMillrace(m => m.UseInMemoryStorage().AddWorkflow<NotAWorkflow>()));

        Assert.Contains("IWorkflow", ex.Message);
    }

    [Fact]
    public void Registered_definitions_are_resolvable_through_the_container()
    {
        var services = new ServiceCollection();
        services.AddMillrace(m => m.UseInMemoryStorage().AddWorkflow<V1>().AddWorkflow<V2>());

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<WorkflowRegistry>();

        Assert.Equal(2, registry.Definitions.Count);
        Assert.Equal(2, registry.GetLatest("invoice-approval")?.Version);
    }
}
