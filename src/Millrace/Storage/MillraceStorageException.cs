namespace Millrace.Storage;

/// <summary>
/// Base type for the storage exceptions the contract mandates. Providers are not required to
/// wrap transient/infrastructure errors. Style rule: return-value signaling (<c>ApplyAsync</c>
/// false, <c>TryFireRecurringAsync</c> false, <c>TryCancelAsync</c> false, renewal omissions)
/// is for expected multi-node races where the loser simply drops; exceptions are for caller
/// contract violations or conflicts requiring caller reaction.
/// </summary>
public class MillraceStorageException : Exception
{
    public MillraceStorageException(string message)
        : base(message)
    {
    }

    public MillraceStorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A conditional write matched nothing: <c>UpdateInstanceAsync</c> with a stale revision
/// <em>or</em> a missing instance (providers must not distinguish — one statement can't), and
/// <c>CreateInstanceAsync</c> with a duplicate id (a conflicting write at revision zero).
/// Callers reload and retry their merge.
/// </summary>
public sealed class MillraceConcurrencyException(string message) : MillraceStorageException(message);

/// <summary>
/// An <see cref="JobState.Awaiting"/> insert referenced a <see cref="JobRecord.ParentId"/> that
/// does not exist (thrown by <c>EnqueueAsync</c> and the <c>JobTransition.Enqueue</c> insert
/// path during the continuation fixup).
/// </summary>
public sealed class MillraceParentJobNotFoundException(JobId parentId)
    : MillraceStorageException($"Continuation parent job '{parentId}' does not exist.")
{
    public JobId ParentId { get; } = parentId;
}
