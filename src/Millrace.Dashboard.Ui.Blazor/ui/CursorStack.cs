namespace Millrace.Dashboard.Ui.Blazor.App;

/// <summary>
/// Next/previous paging over opaque cursors.
/// </summary>
/// <remarks>
/// There is no page number and no total, because §11.12 does not ship one. "Previous" works by
/// keeping the cursors already visited on a stack rather than by arithmetic — the same approach the
/// React and Angular UIs take, for the same reason: a cursor is not an offset and cannot be
/// decremented.
/// </remarks>
public sealed class CursorStack
{
    private readonly List<string?> _visited = [null];

    /// <summary>The cursor for the page currently shown; null for the first.</summary>
    public string? Cursor => _visited[^1];

    public bool CanGoBack => _visited.Count > 1;

    public void Next(string cursor) => _visited.Add(cursor);

    public void Back()
    {
        if (CanGoBack)
        {
            _visited.RemoveAt(_visited.Count - 1);
        }
    }

    /// <summary>Called when a filter changes: the cursors already visited describe the old query.</summary>
    public void Reset()
    {
        _visited.Clear();
        _visited.Add(null);
    }
}
