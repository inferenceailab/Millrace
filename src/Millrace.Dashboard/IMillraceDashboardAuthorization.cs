using Microsoft.AspNetCore.Http;

namespace Millrace.Dashboard;

/// <summary>
/// Decides who may reach the dashboard API (ARCHITECTURE.md §11.13).
/// </summary>
/// <remarks>
/// <para>
/// Registering an implementation is what satisfies the startup requirement: mounting the dashboard
/// with no implementation registered throws outside Development, because the API exposes serialized
/// job arguments — routinely personal data — and gains cancel, requeue and trigger actions in 0.4.
/// </para>
/// <para>
/// This runs on every dashboard request, including the OpenAPI document. Keep it cheap; it is not a
/// place for a per-request database round trip unless you cache.
/// </para>
/// </remarks>
public interface IMillraceDashboardAuthorization
{
    /// <summary>
    /// Whether this request may proceed. Returning <see langword="false"/> yields <c>404</c>, not
    /// <c>403</c> — an ops surface should not confirm its own existence to an unauthorized caller.
    /// </summary>
    ValueTask<bool> AuthorizeAsync(HttpContext context, CancellationToken ct);
}
