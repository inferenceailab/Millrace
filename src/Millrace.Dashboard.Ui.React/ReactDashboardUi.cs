using System.Reflection;
using Millrace.Dashboard;

namespace Millrace.Dashboard.Ui.React;

/// <summary>
/// Serves the prebuilt React bundle from embedded resources.
/// </summary>
/// <remarks>
/// The bundle is compiled into this assembly at build time (see the csproj), so a consumer
/// installs one NuGet package and never touches Node, npm or a CDN — §7's requirement, and the
/// reason the assets are resources rather than files on disk.
/// </remarks>
internal sealed class ReactDashboardUi : IMillraceDashboardUi
{
    private const string Prefix = "ui/";
    private const string EntryDocument = "ui/index.html";

    private static readonly Assembly Assembly = typeof(ReactDashboardUi).Assembly;

    /// <summary>
    /// Maps a normalised asset path to its resource name. Built once: the set is fixed at compile
    /// time.
    /// </summary>
    /// <remarks>
    /// Keys are normalised to forward slashes because the embedded names carry whichever separator
    /// MSBuild's <c>%(RecursiveDir)</c> produced on the build machine — backslash on Windows,
    /// forward slash elsewhere. Without this, a package built on Windows would 404 every asset in a
    /// subdirectory.
    /// </remarks>
    private static readonly Dictionary<string, string> Assets = Assembly
        .GetManifestResourceNames()
        .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
        .ToDictionary(name => name.Replace('\\', '/'), name => name, StringComparer.OrdinalIgnoreCase);

    public string Name => "React";

    public bool TryOpenAsset(string relativePath, out Stream content, out string contentType)
    {
        content = Stream.Null;
        contentType = "application/octet-stream";

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0 || normalized.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (!Assets.TryGetValue(Prefix + normalized, out var resourceName))
        {
            return false;
        }

        var stream = Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return false;
        }

        content = stream;
        contentType = ContentTypeFor(normalized);
        return true;
    }

    public Stream OpenEntryDocument(out string contentType)
    {
        contentType = "text/html; charset=utf-8";
        return (Assets.TryGetValue(EntryDocument, out var name)
                ? Assembly.GetManifestResourceStream(name)
                : null)
            ?? throw new InvalidOperationException(
                $"The React bundle is missing '{EntryDocument}'. The package was built without its UI assets — "
                + "rebuild Millrace.Dashboard.Ui.React with Node available and without -p:SkipUiBuild=true.");
    }

    private static string ContentTypeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".ico" => "image/x-icon",
            ".woff2" => "font/woff2",
            ".map" => "application/json; charset=utf-8",
            _ => "application/octet-stream",
        };
}
