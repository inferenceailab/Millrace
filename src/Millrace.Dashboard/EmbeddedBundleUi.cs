using System.Reflection;

namespace Millrace.Dashboard;

/// <summary>
/// A <see cref="IMillraceDashboardUi"/> whose assets are embedded resources under <c>ui/</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every official UI package ships the same way — a prebuilt bundle compiled into the assembly, so a
/// consumer installs one NuGet package and never touches Node, npm or a CDN (§7). Only the assembly
/// and the name differ, so the serving logic lives here once rather than three times: three copies
/// would drift, and the drift would show up as one framework's dashboard 404ing an asset the others
/// serve.
/// </para>
/// <para>
/// A UI package that is not a bundle of static files is free to implement the interface directly;
/// this is a convenience for the shape all three official ones happen to have.
/// </para>
/// </remarks>
/// <param name="assembly">The assembly holding the embedded bundle.</param>
/// <param name="name">The UI's name, for the startup log line — e.g. "React".</param>
public abstract class EmbeddedBundleUi(Assembly assembly, string name) : IMillraceDashboardUi
{
    private const string Prefix = "ui/";
    private const string EntryDocument = "ui/index.html";

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
    private readonly Dictionary<string, string> _assets = assembly
        .GetManifestResourceNames()
        .Where(resource => resource.StartsWith(Prefix, StringComparison.Ordinal))
        .ToDictionary(
            resource => resource.Replace('\\', '/'), resource => resource, StringComparer.OrdinalIgnoreCase);

    public string Name { get; } = name;

    public bool TryOpenAsset(string relativePath, out Stream content, out string contentType)
    {
        content = Stream.Null;
        contentType = "application/octet-stream";

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0 || normalized.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (!_assets.TryGetValue(Prefix + normalized, out var resourceName))
        {
            return false;
        }

        var stream = assembly.GetManifestResourceStream(resourceName);
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
        return (_assets.TryGetValue(EntryDocument, out var resourceName)
                ? assembly.GetManifestResourceStream(resourceName)
                : null)
            ?? throw new InvalidOperationException(
                $"The {Name} bundle is missing '{EntryDocument}'. The package was built without its UI "
                + $"assets — rebuild {assembly.GetName().Name} with Node available and without "
                + "-p:SkipUiBuild=true.");
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
