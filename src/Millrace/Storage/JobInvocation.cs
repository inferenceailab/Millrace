namespace Millrace.Storage;

/// <summary>
/// The serialized form of a captured job call: which method to invoke and with what arguments.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TypeName"/> and every entry of <see cref="ParameterTypes"/> render type references
/// as <c>Namespace.Type[+Nested], AssemblySimpleName</c> with Version/Culture/PublicKeyToken
/// omitted at every level — including generic type arguments and array element types — so
/// invocations survive assembly version bumps. <see cref="TypeName"/> is captured from the
/// declared service type at the call site (the type resolved from DI), never from the method's
/// declaring type.
/// </para>
/// <para>
/// <see cref="ArgumentsJson"/> holds one JSON document per parameter. A
/// <see cref="CancellationToken"/> parameter serializes as <see langword="null"/>; the worker
/// injects the job's execution token at invoke time.
/// </para>
/// </remarks>
public sealed record JobInvocation
{
    public required string TypeName { get; init; }

    public required string MethodName { get; init; }

    public required IReadOnlyList<string> ParameterTypes { get; init; }

    public required IReadOnlyList<string?> ArgumentsJson { get; init; }
}
