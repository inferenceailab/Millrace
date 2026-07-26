using Millrace.Dashboard.Ui.Angular;
using Millrace.Dashboard.Ui.React;
using Xunit;

namespace Millrace.Dashboard.Tests;

/// <summary>
/// A UI package ships the bundle and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Each UI project keeps a whole npm tree under <c>ui/</c>, and the SDK's default <c>**/*.cs</c>
/// glob does not know that tree is not ours. It reached into <c>node_modules</c> and compiled
/// node-gyp's <c>Find-VisualStudio.cs</c>, which put 52 public COM interop types — <c>ISetupInstance2</c>
/// and friends — into <c>Millrace.Dashboard.Ui.Angular.dll</c> and into the published package.
/// </para>
/// <para>
/// Nothing caught it. It compiled cleanly, the bundle still worked, and the only signal was 52
/// CS1591 warnings hidden inside a suppression that exists for an unrelated reason (#99). The
/// projects already excluded <c>node_modules</c> from <c>None</c> items, which is what makes this
/// worth a test rather than a comment: the exclusion looked complete.
/// </para>
/// <para>
/// Checked by namespace rather than against a list of expected types, because the failure is one of
/// addition — a dependency that vendors a <c>.cs</c> file is added by someone who is not thinking
/// about this assembly's public surface, and a list would be updated to match rather than read as
/// the alarm it is.
/// </para>
/// </remarks>
public sealed class UiPackagingTests
{
    /// <summary>
    /// The only two namespaces this repo deliberately puts public types in.
    /// </summary>
    /// <remarks>
    /// <c>Microsoft.Extensions.DependencyInjection</c> is the registration convention — it is what
    /// makes <c>services.AddMillraceReactUi()</c> resolve without a using directive, and all four
    /// service-collection extension classes in <c>src/</c> follow it. Everything else we write is
    /// under <c>Millrace</c>.
    /// </remarks>
    private static bool IsOurs(string? candidate) =>
        candidate is not null
        && (candidate == "Millrace"
            || candidate.StartsWith("Millrace.", StringComparison.Ordinal)
            || candidate == "Microsoft.Extensions.DependencyInjection");

    public static TheoryData<string, Type> Uis => new()
    {
        { "React", typeof(ReactDashboardUi) },
        { "Angular", typeof(AngularDashboardUi) },
    };

    [Theory]
    [MemberData(nameof(Uis))]
    public void A_ui_assembly_exports_only_its_own_types(string name, Type marker)
    {
        var strays = marker.Assembly.GetExportedTypes()
            .Where(type => !IsOurs(type.Namespace))
            .Select(type => type.FullName ?? type.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            strays.Count == 0,
            $"The {name} UI assembly exports {strays.Count} type(s) from outside Millrace: "
            + $"{string.Join(", ", strays)}. Something under ui/ is being compiled into the package — "
            + "check the Compile glob in the .csproj before these reach a consumer.");
    }
}
