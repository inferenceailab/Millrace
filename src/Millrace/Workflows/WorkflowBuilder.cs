using System.Linq.Expressions;
using Millrace.Invocations;

namespace Millrace.Workflows;

/// <summary>
/// Runtime bindings for one node — the executable half the exported shape deliberately omits.
/// </summary>
internal sealed class NodeBinding<TData>
{
    public Type? ActivityType { get; init; }

    public Func<TData, bool>? Condition { get; init; }

    public Func<TData, System.Collections.IEnumerable>? Collection { get; init; }

    public Func<TData, string>? Correlate { get; init; }

    /// <summary>Deserializes the JSON payload and applies it to the document.</summary>
    public Action<TData, string>? BindPayload { get; init; }

    public Type? PayloadType { get; init; }
}

/// <summary>
/// Accumulates nodes while a definition builds itself. Shared by the root builder and every nested
/// branch builder, so ids stay unique and generation order stays deterministic.
/// </summary>
internal sealed class GraphBuilderState<TData>
{
    private readonly Dictionary<WorkflowNodeKind, int> _counters = [];

    public List<WorkflowNode> Nodes { get; } = [];

    public Dictionary<string, NodeBinding<TData>> Bindings { get; } = [];

    /// <summary>
    /// Deterministic ids from build order. <c>Build</c> is ordinary code run the same way every
    /// time, so the same definition yields the same ids in every process — which persisted cursors
    /// depend on.
    /// </summary>
    public string NextId(WorkflowNodeKind kind)
    {
        _counters.TryGetValue(kind, out var n);
        _counters[kind] = ++n;
        var prefix = kind switch
        {
            WorkflowNodeKind.Activity => "a",
            WorkflowNodeKind.If => "if",
            WorkflowNodeKind.Parallel => "par",
            WorkflowNodeKind.ForEach => "each",
            WorkflowNodeKind.Delay => "delay",
            WorkflowNodeKind.WaitForSignal => "sig",
            _ => "n",
        };
        return $"{prefix}{n}";
    }
}

/// <summary>
/// The fluent builder. Each instance owns one sequence; nested sequences get their own builder over
/// the same shared state.
/// </summary>
internal sealed class WorkflowBuilder<TData> : IWorkflowBuilder<TData>
{
    private readonly GraphBuilderState<TData> _state;
    private readonly List<string> _sequence = [];

    public WorkflowBuilder(GraphBuilderState<TData> state) => _state = state;

    /// <summary>Entry node of this sequence, or null if it is empty.</summary>
    public string? Entry => _sequence.Count > 0 ? _sequence[0] : null;

    public IWorkflowBuilder<TData> StartWith<TActivity>() where TActivity : IActivity<TData>
        => Then<TActivity>();

    public IWorkflowBuilder<TData> Then<TActivity>() where TActivity : IActivity<TData>
    {
        var id = _state.NextId(WorkflowNodeKind.Activity);
        Append(new WorkflowNode
        {
            Id = id,
            Kind = WorkflowNodeKind.Activity,
            ActivityType = TypeNameFormatter.Format(typeof(TActivity)),
        });
        _state.Bindings[id] = new NodeBinding<TData> { ActivityType = typeof(TActivity) };
        return this;
    }

    public IWorkflowBuilder<TData> If(
        Expression<Func<TData, bool>> condition,
        Action<IWorkflowBuilder<TData>> then,
        Action<IWorkflowBuilder<TData>>? otherwise = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(then);

        var id = _state.NextId(WorkflowNodeKind.If);
        var whenTrue = BuildBranch(then);
        var whenFalse = otherwise is null ? null : BuildBranch(otherwise);

        Append(new WorkflowNode
        {
            Id = id,
            Kind = WorkflowNodeKind.If,
            Condition = Render(condition.Body),
            WhenTrue = whenTrue,
            WhenFalse = whenFalse,
        });
        _state.Bindings[id] = new NodeBinding<TData> { Condition = condition.Compile() };
        return this;
    }

    public IWorkflowBuilder<TData> Parallel(params Action<IWorkflowBuilder<TData>>[] branches)
    {
        ArgumentNullException.ThrowIfNull(branches);
        if (branches.Length < 2)
        {
            throw new ArgumentException(
                "Parallel needs at least two branches; one branch is a plain sequence.", nameof(branches));
        }

        var id = _state.NextId(WorkflowNodeKind.Parallel);
        var entries = branches.Select(BuildBranch).Where(e => e is not null).Select(e => e!).ToList();

        Append(new WorkflowNode
        {
            Id = id,
            Kind = WorkflowNodeKind.Parallel,
            Branches = entries,
        });
        return this;
    }

    public IWorkflowBuilder<TData> ForEach<TItem>(
        Expression<Func<TData, IEnumerable<TItem>>> collection,
        Action<IWorkflowBuilder<TData>> body)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(body);

        var id = _state.NextId(WorkflowNodeKind.ForEach);
        var bodyEntry = BuildBranch(body);
        var compiled = collection.Compile();

        Append(new WorkflowNode
        {
            Id = id,
            Kind = WorkflowNodeKind.ForEach,
            Collection = Render(collection.Body),
            Body = bodyEntry,
        });
        _state.Bindings[id] = new NodeBinding<TData>
        {
            Collection = data => (System.Collections.IEnumerable)(compiled(data) ?? Array.Empty<TItem>()),
        };
        return this;
    }

    public IWorkflowBuilder<TData> Delay(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration), duration, "A delay must be positive; a non-positive delay is just the next step.");
        }

        Append(new WorkflowNode
        {
            Id = _state.NextId(WorkflowNodeKind.Delay),
            Kind = WorkflowNodeKind.Delay,
            Delay = duration,
        });
        return this;
    }

    public IWorkflowBuilder<TData> WaitForSignal<TPayload>(
        string name,
        Expression<Func<TData, string>> correlate,
        Action<TData, TPayload> bind,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(correlate);
        ArgumentNullException.ThrowIfNull(bind);
        if (timeout is { } t && t <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), t, "A signal timeout must be positive.");
        }

        var id = _state.NextId(WorkflowNodeKind.WaitForSignal);
        Append(new WorkflowNode
        {
            Id = id,
            Kind = WorkflowNodeKind.WaitForSignal,
            SignalName = name,
            PayloadType = TypeNameFormatter.Format(typeof(TPayload)),
            Timeout = timeout,
        });

        var compiledCorrelate = correlate.Compile();
        _state.Bindings[id] = new NodeBinding<TData>
        {
            Correlate = compiledCorrelate,
            PayloadType = typeof(TPayload),
            // Payloads travel as JSON so external senders stay possible; the typed binder is applied
            // on this side of that boundary.
            BindPayload = (data, json) =>
            {
                var payload = System.Text.Json.JsonSerializer.Deserialize<TPayload>(json);
                if (payload is not null)
                {
                    bind(data, payload);
                }
            },
        };
        return this;
    }

    /// <summary>Builds a nested sequence and returns its entry node id.</summary>
    private string? BuildBranch(Action<IWorkflowBuilder<TData>> configure)
    {
        var branch = new WorkflowBuilder<TData>(_state);
        configure(branch);
        return branch.Entry;
    }

    /// <summary>Adds a node and links the previous node in this sequence to it.</summary>
    private void Append(WorkflowNode node)
    {
        if (_sequence.Count > 0)
        {
            var previousId = _sequence[^1];
            var index = _state.Nodes.FindIndex(n => n.Id == previousId);
            _state.Nodes[index] = _state.Nodes[index] with { Next = node.Id };
        }

        _state.Nodes.Add(node);
        _sequence.Add(node.Id);
    }

    /// <summary>
    /// Renders an expression for display in the exported shape. Trims the compiler's parameter name
    /// so a condition reads as <c>Amount &gt; 10000</c> rather than <c>d.Amount &gt; 10000</c>.
    /// </summary>
    private static string Render(Expression body)
    {
        var text = body.ToString();
        var arrow = text.IndexOf("=>", StringComparison.Ordinal);
        return arrow >= 0 ? text[(arrow + 2)..].Trim() : text;
    }
}
