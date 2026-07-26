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

    /// <summary>Identity of the workflow this defines.</summary>
    public string Id { get; }

    /// <summary>Version of this definition; with <see cref="Id"/> it is the registry key.</summary>
    public int Version { get; }

    /// <summary>The <c>TData</c> this workflow carries.</summary>
    /// <remarks>
    /// Kept as a <see cref="Type"/> because a compiled definition is handled without knowing its
    /// data type statically — the engine deserializes an instance's stored document against this.
    /// It is also the part of a definition that cannot be exported: the graph shape serializes,
    /// a type binding does not.
    /// </remarks>
    public Type DataType { get; }

    /// <summary>The serializable shape — what the dashboard renders and a designer would edit.</summary>
    public WorkflowGraph Graph { get; }

    /// <summary>
    /// The graph as JSON. Deliberately the whole export surface: everything a renderer needs, and
    /// nothing that would tie it to this process.
    /// </summary>
    public string ExportGraph(JsonSerializerOptions? options = null)
        => JsonSerializer.Serialize(Graph, options ?? DefaultExportOptions);

    /// <summary>
    /// Wires the last node of each <c>If</c> arm to whatever follows the <c>If</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An arm always rejoins — unlike a <c>Parallel</c> branch, which must wait at a join — so the
    /// rejoin is a static property of the graph and belongs in the shape rather than in a runtime
    /// return-address stack. It also makes the exported graph read correctly: an arm visibly flows
    /// into the step after the condition.
    /// </para>
    /// <para>
    /// Processed in reverse build order, which is outermost-first: the builder emits a branch's
    /// nodes before the node that owns them. An inner <c>If</c> must learn its own continuation from
    /// the outer one before wiring its own arms, or a nested arm ends up pointing at the null the
    /// inner <c>If</c> had at the time.
    /// </para>
    /// </remarks>
    private static List<WorkflowNode> LinkBranchExits(List<WorkflowNode> nodes)
    {
        var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            var branchNode = byId[nodes[i].Id];
            if (branchNode.Kind is not (WorkflowNodeKind.If or WorkflowNodeKind.Saga))
            {
                continue;
            }

            foreach (var arm in new[] { branchNode.WhenTrue, branchNode.WhenFalse, branchNode.Body })
            {
                if (arm is null)
                {
                    continue;
                }

                var terminal = arm;
                while (byId[terminal].Next is { } next)
                {
                    terminal = next;
                }

                byId[terminal] = byId[terminal] with { Next = branchNode.Next };
            }
        }

        return nodes.Select(n => byId[n.Id]).ToList();
    }

    /// <summary>
    /// Marks every node inside a saga's body with that saga's id.
    /// </summary>
    /// <remarks>
    /// A dead-lettered job carries only its own node id, so the engine has to find the enclosing
    /// saga from the graph. Computing it once here beats threading it through every job — and it
    /// cannot drift, because it is derived from the same structure the walk uses.
    /// </remarks>
    private static List<WorkflowNode> AssignSagas(List<WorkflowNode> nodes)
    {
        var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

        foreach (var saga in nodes.Where(n => n.Kind == WorkflowNodeKind.Saga))
        {
            // Follow the body's sequence only. A step's own Next leaves the saga once the body ends,
            // so the walk stops where the saga does.
            for (var id = saga.Body; id is not null; id = byId[id].Next)
            {
                if (byId[id].SagaId is not null)
                {
                    break; // already claimed by an inner saga
                }

                byId[id] = byId[id] with { SagaId = saga.Id };
                if (byId[id].Next is null || byId[id].Next == saga.Next)
                {
                    break;
                }
            }
        }

        return nodes.Select(n => byId[n.Id]).ToList();
    }

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
            Nodes = AssignSagas(LinkBranchExits(state.Nodes)),
        };

        return new WorkflowDefinition<TData>(
            workflow.Id, workflow.Version, graph, state.Bindings, state.Compensations);
    }
}

/// <summary>A compiled definition over a known data type.</summary>
public sealed class WorkflowDefinition<TData> : WorkflowDefinition
{
    internal WorkflowDefinition(
        string id, int version, WorkflowGraph graph,
        IReadOnlyDictionary<string, NodeBinding<TData>> bindings,
        IReadOnlyDictionary<string, Type> compensations)
        : base(id, version, typeof(TData), graph)
    {
        Bindings = bindings;
        Compensations = compensations;
    }

    /// <summary>Compensating activity type per saga step, by step node id.</summary>
    internal IReadOnlyDictionary<string, Type> Compensations { get; }

    /// <summary>
    /// Executable bindings by node id. Internal: they are engine state, and exposing them would
    /// invite callers to depend on behaviour the exported shape deliberately does not carry.
    /// </summary>
    internal IReadOnlyDictionary<string, NodeBinding<TData>> Bindings { get; }
}
