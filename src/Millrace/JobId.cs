using System.Text.Json;
using System.Text.Json.Serialization;

namespace Millrace;

/// <summary>
/// Identifies a single job in the substrate. Generated exclusively by the engine — storage
/// providers persist ids verbatim, treat them as opaque, and never mint or order by them.
/// Version 7 GUIDs keep storage indexes append-friendly.
/// </summary>
[JsonConverter(typeof(JobIdJsonConverter))]
public readonly record struct JobId(Guid Value)
{
    /// <summary>Mints an id from the system clock.</summary>
    public static JobId New() => new(Guid.CreateVersion7());

    /// <summary>Mints an id timestamped from <paramref name="time"/>.</summary>
    /// <remarks>
    /// The overload the engine uses, so that a test driving a fake clock produces ids whose
    /// embedded timestamps agree with the rest of the record it is building. Nothing in the
    /// contract lets a provider read that timestamp back — it exists to keep index writes local,
    /// not to be interpreted.
    /// </remarks>
    public static JobId New(TimeProvider time) => new(Guid.CreateVersion7(time.GetUtcNow()));

    /// <summary>Renders the id as 32 hex digits without hyphens.</summary>
    /// <remarks>
    /// The compact form, for logs and URLs. JSON uses the hyphenated one via
    /// <see cref="JobIdJsonConverter"/>; both round-trip through <see cref="Guid.Parse(string)"/>,
    /// so the difference is presentational rather than a validation boundary.
    /// </remarks>
    public override string ToString() => Value.ToString("n");
}

/// <summary>Serializes <see cref="JobId"/> as a bare GUID string rather than an object.</summary>
public sealed class JobIdJsonConverter : JsonConverter<JobId>
{
    /// <inheritdoc />
    public override JobId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetGuid());

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, JobId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);

    /// <inheritdoc />
    /// <remarks>
    /// A separate path because property names are read as raw strings — the reader offers no
    /// <c>GetGuid</c> there, so the parse is explicit.
    /// </remarks>
    public override JobId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(Guid.Parse(reader.GetString()!));

    /// <inheritdoc />
    public override void WriteAsPropertyName(Utf8JsonWriter writer, JobId value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.Value.ToString("d"));
}
