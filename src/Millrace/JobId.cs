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
    public static JobId New() => new(Guid.CreateVersion7());

    public static JobId New(TimeProvider time) => new(Guid.CreateVersion7(time.GetUtcNow()));

    public override string ToString() => Value.ToString("n");
}

/// <summary>Serializes <see cref="JobId"/> as a bare GUID string rather than an object.</summary>
public sealed class JobIdJsonConverter : JsonConverter<JobId>
{
    public override JobId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetGuid());

    public override void Write(Utf8JsonWriter writer, JobId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);

    public override JobId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(Guid.Parse(reader.GetString()!));

    public override void WriteAsPropertyName(Utf8JsonWriter writer, JobId value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.Value.ToString("d"));
}
