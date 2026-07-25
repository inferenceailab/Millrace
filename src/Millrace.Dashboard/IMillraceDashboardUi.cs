namespace Millrace.Dashboard;

/// <summary>
/// A set of prebuilt UI assets served under the dashboard mount.
/// </summary>
/// <remarks>
/// <para>
/// Implemented by the official UI packages (React, Angular, Blazor), each shipping an embedded
/// prebuilt bundle so consumers never install Node (§7). A consumer references the backend plus
/// exactly one UI package; registering it is what makes <c>MapMillraceDashboard</c> serve a UI at
/// all, so the mount stays a single call and the prefix is never repeated.
/// </para>
/// <para>
/// Assets are static and versioned with the package, so implementations are expected to resolve
/// from memory or embedded resources rather than the filesystem.
/// </para>
/// </remarks>
public interface IMillraceDashboardUi
{
    /// <summary>The UI's name, for the startup log line — e.g. "React".</summary>
    string Name { get; }

    /// <summary>
    /// Opens the asset at <paramref name="relativePath"/> (no leading slash), or returns
    /// <see langword="false"/> if this bundle has no such asset.
    /// </summary>
    /// <remarks>
    /// A miss is normal, not an error: client-side routes look like paths but have no asset, and
    /// the caller falls back to the entry document so a deep link still loads the application.
    /// </remarks>
    bool TryOpenAsset(string relativePath, out Stream content, out string contentType);

    /// <summary>
    /// The entry document served for the mount root and for any path with no matching asset.
    /// </summary>
    Stream OpenEntryDocument(out string contentType);
}
