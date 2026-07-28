using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Millrace.Storage;
using Millrace.Storage.Monitoring;
using Xunit;

namespace Millrace.Dashboard.Tests;

/// <summary>
/// Pins the set of enums a dashboard client has to be able to read.
/// </summary>
/// <remarks>
/// <para>
/// §11.24 added wire-format tests after enum values shipped as integers and silently removed every
/// status colour from the dashboard. Those tests cover the TypeScript clients, which mirror the
/// contract by hand. The Blazor client does not mirror anything — it deserializes into the contract
/// types themselves — and that turned out to hide a worse version of the same bug: it used default
/// <see cref="JsonSerializerOptions"/>, so it could not read a single string enum the server writes.
/// It compiled, the §11.22 parity check passed because every route literal was present, and nothing
/// executed a response until the UI was opened in a browser.
/// </para>
/// <para>
/// The fix is a converter per enum, and the trap is that the list has to be complete: four separate
/// enums are reachable from these responses, and each missing one fails only on the view that
/// happens to read it. So this walks the contract and asserts the set, rather than trusting anyone
/// to remember. A new enum on a response type fails here with the file to change.
/// </para>
/// </remarks>
public sealed class BlazorWireFormatTests
{
    /// <summary>Every type the dashboard's GET endpoints return.</summary>
    private static readonly Type[] Responses =
    [
        typeof(Page<JobSummary>),
        typeof(Page<RecurringSummary>),
        typeof(Page<WorkflowInstanceSummary>),
        typeof(JobDetails),
        typeof(JobStatistics),
    ];

    /// <summary>
    /// The enums <c>DashboardClient.JsonOptions</c> registers a <c>JsonStringEnumConverter&lt;T&gt;</c>
    /// for. Kept here as names because the Blazor app is a WebAssembly project this test cannot
    /// reference — what is being pinned is the contract's shape, and the client is what must follow.
    /// </summary>
    private static readonly string[] Covered =
    [
        nameof(JobState),
        nameof(WorkflowInstanceState),
        nameof(JobAttemptOutcome),
        nameof(RetryKind),
    ];

    [Fact]
    public void Every_enum_reachable_from_a_response_has_a_converter_on_the_Blazor_client()
    {
        var reachable = Responses
            .SelectMany(Reachable)
            .Where(t => t.IsEnum)
            .Select(t => t.Name)
            .Distinct()
            .Order()
            .ToArray();

        Assert.True(
            reachable.SequenceEqual(Covered.Order()),
            $"""
             The enums reachable from the dashboard's responses have changed.

               reachable: {string.Join(", ", reachable)}
               covered:   {string.Join(", ", Covered.Order())}

             Every one of them needs a JsonStringEnumConverter<T> in DashboardClient.JsonOptions
             (src/Millrace.Dashboard.Ui.Blazor/ui/DashboardClient.cs), and this list updating to
             match. The non-generic JsonStringEnumConverter is not a substitute: it builds converters
             by reflection, and a published Blazor app is trimmed, so it silently falls back to
             reading enums as numbers.
             """);
    }

    [Fact]
    public void The_servers_own_options_read_the_strings_it_writes()
    {
        // The client's options are a second declaration of the server's, so the shape they both have
        // to agree on is worth stating once: written as a name, read as a name.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter<JobState>() },
        };

        var json = JsonSerializer.Serialize(JobState.Succeeded, options);

        Assert.Equal("\"Succeeded\"", json);
        Assert.Equal(JobState.Succeeded, JsonSerializer.Deserialize<JobState>(json, options));
    }

    /// <summary>Walks a response type's properties, following collections and nested records.</summary>
    private static IEnumerable<Type> Reachable(Type type)
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>([type]);

        while (queue.Count > 0)
        {
            var current = Unwrap(queue.Dequeue());
            if (!seen.Add(current))
            {
                continue;
            }

            yield return current;

            // Only walk into the contract's own types. Following the BCL would wander into every
            // enum in the framework and prove nothing about this wire format.
            if (current.Assembly != typeof(JobSummary).Assembly)
            {
                continue;
            }

            foreach (var property in current.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                queue.Enqueue(property.PropertyType);
            }
        }
    }

    /// <summary>Reduces <c>List&lt;T&gt;</c>, <c>T?</c> and dictionaries to the types inside them.</summary>
    private static Type Unwrap(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            return underlying;
        }

        if (type.IsArray)
        {
            return type.GetElementType()!;
        }

        // A dictionary keyed by an enum matters as much as a value: JobStatistics.JobsByState is
        // keyed by JobState, and that is the read the overview tiles depend on.
        return type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type)
            ? type.GetGenericArguments()[0]
            : type;
    }
}
