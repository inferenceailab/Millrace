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
