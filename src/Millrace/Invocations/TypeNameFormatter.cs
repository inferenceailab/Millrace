using System.Text;

namespace Millrace.Invocations;

/// <summary>
/// Renders and resolves version-free type references for the invocation wire format:
/// <c>Namespace.Type[+Nested], AssemblySimpleName</c> with Version/Culture/PublicKeyToken
/// omitted at every level — including generic type arguments and array element types — so
/// stored invocations survive assembly version bumps. Never emits raw
/// <see cref="Type.FullName"/> for constructed generics (which embeds fully-qualified argument
/// names).
/// </summary>
public static class TypeNameFormatter
{
    /// <summary>
    /// Renders a version-free reference to <paramref name="type"/>.
    /// </summary>
    /// <remarks>
    /// The result is a persisted identifier, not a display string — it is written into job records
    /// and read back by a different process, usually after at least one redeploy. Version, culture
    /// and public key token are dropped at every level so a package bump does not orphan in-flight
    /// jobs; namespace, type name and assembly simple name are all load-bearing, which is the
    /// reason renaming a type that queued jobs still refer to is a breaking deploy.
    /// </remarks>
    public static string Format(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var builder = new StringBuilder();
        AppendName(type, builder);
        builder.Append(", ").Append(type.Assembly.GetName().Name);
        return builder.ToString();
    }

    /// <summary>
    /// Loads the type a stored reference names, reversing <see cref="Format"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The type cannot be loaded — renamed, moved to another assembly, or living in one this
    /// process never loads. The underlying failure is wrapped rather than surfaced raw because it
    /// names only the string it was handed, while the fact worth reporting is that a deploy moved a
    /// type out from under jobs that were already queued against it.
    /// </exception>
    public static Type Resolve(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        try
        {
            return Type.GetType(typeName, throwOnError: true)!;
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                $"Cannot resolve job type '{typeName}'. The type may have been renamed or its " +
                "assembly is not loaded — renaming types used by in-flight jobs is a breaking deploy.", e);
        }
    }

    private static void AppendName(Type type, StringBuilder builder)
    {
        if (type.IsArray)
        {
            AppendName(type.GetElementType()!, builder);
            builder.Append('[').Append(',', type.GetArrayRank() - 1).Append(']');
            return;
        }

        if (type.IsConstructedGenericType)
        {
            // The generic *definition*'s FullName is already version-free (List`1).
            builder.Append(type.GetGenericTypeDefinition().FullName);
            builder.Append('[');
            var arguments = type.GetGenericArguments();
            for (var i = 0; i < arguments.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append('[');
                AppendName(arguments[i], builder);
                builder.Append(", ").Append(arguments[i].Assembly.GetName().Name);
                builder.Append(']');
            }

            builder.Append(']');
            return;
        }

        builder.Append(type.FullName);
    }
}
