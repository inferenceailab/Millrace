using System.Text.Json.Nodes;
using Xunit;

namespace Millrace.Storage.Verification;

/// <summary>
/// Semantic JSON comparison for the suites.
/// </summary>
/// <remarks>
/// Data and cursor documents are JSON documents, not opaque strings (§11.9): a provider may store
/// them in a native JSON column and normalise whitespace and key order, so fidelity is asserted
/// semantically. Shared rather than duplicated per suite — two copies of this rule would eventually
/// disagree about what "unchanged" means.
/// </remarks>
internal static class JsonAssert
{
    public static void Equal(string expected, string? actual)
    {
        Assert.NotNull(actual);
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
            $"JSON documents differ semantically. Expected: {expected} Actual: {actual}");
    }
}
