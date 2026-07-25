using System.Text.Json;

namespace Millrace.Workflows;

/// <summary>
/// A compiled workflow definition: the exported shape plus the runtime bindings that shape omits.
/// </summary>
public abstract class WorkflowDefinition
{
    private protected WorkflowDefinition(string id, int version, Type dataType, WorkflowGraph graph)
    {
        Id = id;
        Version = version;
        DataType = dataType;
        Graph = graph;
    }

    public string Id { get; }

    public int Version { get; }

    public Type DataType { get; }

    /// <summary>The serializable shape — what the dashboard renders and a designer would edit.</summary>
    public WorkflowGraph Graph { get; }

    /// <summary>
    /// The graph as JSON. Deliberately the whole export surface: everything a renderer needs, and
    /// nothing that would tie it to this process.
    /// </summary>
    public string ExportGraph(JsonSerializerOptions? options = null)
        => JsonSerializer.Serialize(Graph, options ?? DefaultExportOptions);

    private static readonly JsonSerializerOptions DefaultExportOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Compiles an <see cref="IWorkflow{TData}"/> into a definition.</summary>
    /// <exception cref="ArgumentException">The definition is invalid — see the message.</exception>
    public static WorkflowDefinition Compile<TData>(IWorkflow<TData> workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        if (string.IsNullOrWhiteSpace(workflow.Id))
        {
            throw new ArgumentException(
                $"Workflow {workflow.GetType().Name} has no Id. The id keys the definition and every "
                + "instance started from it, so it cannot be blank.", nameof(workflow));
        }

        if (workflow.Version < 1)
        {
            throw new ArgumentException(
                $"Workflow '{workflow.Id}' has version {workflow.Version}. Versions start at 1: "
                + "(Id, Version) keys a definition, and in-flight instances finish on the version they "
                + "started with.", nameof(workflow));
        }

        var state = new GraphBuilderState<TData>();
        var builder = new WorkflowBuilder<TData>(state);
        workflow.Build(builder);

        if (state.Nodes.Count == 0)
        {
            throw new ArgumentException(
                $"Workflow '{workflow.Id}' built no steps. A definition needs at least one activity.",
                nameof(workflow));
        }

        var graph = new WorkflowGraph
        {
            DefinitionId = workflow.Id,
            Version = workflow.Version,
            Start = builder.Entry,
            Nodes = state.Nodes,
        };

        return new WorkflowDefinition<TData>(workflow.Id, workflow.Version, graph, state.Bindings);
    }
}

/// <summary>A compiled definition over a known data type.</summary>
public sealed class WorkflowDefinition<TData> : WorkflowDefinition
{
    internal WorkflowDefinition(
        string id, int version, WorkflowGraph graph, IReadOnlyDictionary<string, NodeBinding<TData>> bindings)
        : base(id, version, typeof(TData), graph)
        => Bindings = bindings;

    /// <summary>
    /// Executable bindings by node id. Internal: they are engine state, and exposing them would
    /// invite callers to depend on behaviour the exported shape deliberately does not carry.
    /// </summary>
    internal IReadOnlyDictionary<string, NodeBinding<TData>> Bindings { get; }
}
