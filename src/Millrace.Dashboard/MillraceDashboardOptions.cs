namespace Millrace.Dashboard;

/// <summary>Configuration for the dashboard backend.</summary>
public sealed class MillraceDashboardOptions
{
    /// <summary>
    /// Serves the dashboard with no authorization at all, in every environment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The deliberate escape hatch from the §11.13 startup requirement, named so it cannot be set by
    /// accident or skimmed past in review. Setting it publishes job payloads and, from 0.4,
    /// management actions to anyone who can reach the mount path.
    /// </para>
    /// <para>
    /// It logs a warning at startup every time. If you want this only for local work, leave it alone
    /// — Development already allows anonymous access without it.
    /// </para>
    /// </remarks>
    public bool AllowAnonymousAccessInsecure { get; set; }
}
