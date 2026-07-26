namespace Millrace.Workflows;

/// <summary>
/// The registered definitions, keyed by <c>(Id, Version)</c>.
/// </summary>
/// <remarks>
/// Old versions stay registered until their instances drain: an in-flight instance always finishes
/// on the version it started with, so removing a version while instances still reference it would
/// strand them (§6.1).
/// </remarks>
public sealed class WorkflowRegistry
{
    private readonly Dictionary<(string Id, int Version), WorkflowDefinition> _definitions;
    private readonly Dictionary<string, int> _latest;

    /// <summary>Builds the registry from every definition registered with the container.</summary>
    /// <remarks>
    /// Also where a duplicate <c>(Id, Version)</c> is caught. That is a startup failure rather than
    /// a last-one-wins merge, because two definitions claiming one key means an instance pinned to
    /// that version could resume into either — and which one it got would depend on registration
    /// order.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Two definitions share an <c>(Id, Version)</c>.
    /// </exception>
    public WorkflowRegistry(IEnumerable<WorkflowDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        _definitions = [];
        _latest = [];

        foreach (var definition in definitions)
        {
            var key = (definition.Id, definition.Version);
            if (!_definitions.TryAdd(key, definition))
            {
                throw new InvalidOperationException(
                    $"Workflow '{definition.Id}' version {definition.Version} is registered twice. "
                    + "(Id, Version) keys a definition — bump the version to change a workflow.");
            }

            if (!_latest.TryGetValue(definition.Id, out var latest) || definition.Version > latest)
            {
                _latest[definition.Id] = definition.Version;
            }
        }
    }

    /// <summary>Every registered definition, all versions of all workflows.</summary>
    /// <remarks>
    /// Includes superseded versions, which are registered precisely because instances still
    /// running are pinned to them — so this is not a list of what is current.
    /// </remarks>
    public IReadOnlyCollection<WorkflowDefinition> Definitions => _definitions.Values;

    /// <summary>The exact version an in-flight instance is pinned to.</summary>
    public bool TryGet(string id, int version, out WorkflowDefinition definition)
        => _definitions.TryGetValue((id, version), out definition!);

    /// <summary>The version a new instance starts on, unless one is pinned explicitly.</summary>
    public WorkflowDefinition? GetLatest(string id)
        => _latest.TryGetValue(id, out var version) && _definitions.TryGetValue((id, version), out var definition)
            ? definition
            : null;
}
