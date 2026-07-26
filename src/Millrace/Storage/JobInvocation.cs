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
    /// <summary>The service type to resolve from the container when the job runs.</summary>
    public required string TypeName { get; init; }

    /// <summary>Name of the method to invoke on it, matched ordinally.</summary>
    /// <remarks>
    /// Not enough to identify the method on its own — overloads are separated by
    /// <see cref="ParameterTypes"/>.
    /// </remarks>
    public required string MethodName { get; init; }

    /// <summary>The parameter list, which is what distinguishes one overload from another.</summary>
    /// <remarks>
    /// Compared as strings, not as resolved types: each candidate's parameters are rendered the
    /// same way and matched ordinally. So the rendering itself is part of the persisted contract —
    /// a change in how a type is formatted would orphan every stored invocation using it, even
    /// though no application code had changed.
    /// </remarks>
    public required IReadOnlyList<string> ParameterTypes { get; init; }

    /// <summary>One JSON document per parameter, positionally aligned with them.</summary>
    /// <remarks>
    /// <see langword="null"/> at a position means the argument is not carried in the record — a
    /// <see cref="CancellationToken"/> is the case that arises today, and the worker supplies the
    /// job's execution token in its place.
    /// </remarks>
    public required IReadOnlyList<string?> ArgumentsJson { get; init; }
}
