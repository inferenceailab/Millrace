using System.Text.Json.Nodes;

namespace Millrace.Workflows;

/// <summary>
/// Three-way merge of a workflow data document.
/// </summary>
/// <remarks>
/// <para>
/// When a branch loses a checkpoint race its activity has already run, and re-running it is exactly
/// what §6.2 says must not happen. What is needed instead is to take the changes that activity made
/// and re-apply them to whatever the winner left behind — a three-way merge of
/// <c>before</c> (the document the activity was handed), <c>after</c> (what it produced), and
/// <c>fresh</c> (what is in storage now).
/// </para>
/// <para>
/// The rule is deliberately narrow: only properties the activity actually changed are written, so a
/// sibling's disjoint edits survive untouched. That is precisely the discipline §6.2 asks of
/// parallel branches — write disjoint regions — and this merge is what makes honouring it pay off.
/// Two branches writing the <em>same</em> property still conflict in the ordinary way: last writer
/// wins, and no merge can do better without a domain-specific rule.
/// </para>
/// </remarks>
internal static class JsonMerge
{
    public static JsonNode? Apply(JsonNode? before, JsonNode? after, JsonNode? fresh)
    {
        // Anything that is not an object merges by replacement: arrays and scalars have no
        // per-member identity to reason about, so the activity's value is the whole change.
        if (before is not JsonObject beforeObject || after is not JsonObject afterObject)
        {
            return after?.DeepClone();
        }

        if (fresh is not JsonObject freshObject)
        {
            return after.DeepClone();
        }

        var result = (JsonObject)freshObject.DeepClone();

        foreach (var property in afterObject)
        {
            var beforeValue = beforeObject[property.Key];
            var afterValue = property.Value;

            if (JsonNode.DeepEquals(beforeValue, afterValue))
            {
                continue; // untouched by this activity — leave the fresh value alone
            }

            result[property.Key] = Apply(beforeValue, afterValue, freshObject[property.Key]);
        }

        // Properties the activity removed.
        foreach (var property in beforeObject)
        {
            if (!afterObject.ContainsKey(property.Key))
            {
                result.Remove(property.Key);
            }
        }

        return result;
    }
}
