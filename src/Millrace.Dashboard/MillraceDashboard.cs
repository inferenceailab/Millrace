namespace Millrace.Dashboard;

/// <summary>Contract identity for the dashboard API.</summary>
public static class MillraceDashboard
{
    /// <summary>
    /// The OpenAPI document name. Prefixed rather than a bare "v1" so it cannot collide with a
    /// document the host application registers.
    /// </summary>
    public const string DocumentName = "millrace-v1";

    /// <summary>
    /// The contract version, carried as a URL segment (§11.11) so the version a client targets is a
    /// routing fact rather than a runtime negotiation. Each official UI pins one.
    /// </summary>
    public const string ApiVersion = "v1";
}
