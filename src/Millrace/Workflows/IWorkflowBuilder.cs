using System.Linq.Expressions;

namespace Millrace.Workflows;

/// <summary>
/// The code-first fluent surface a definition builds itself with (ARCHITECTURE.md §6.1).
/// </summary>
/// <remarks>
/// Every method appends to the current sequence and returns the same builder, so a definition reads
/// top to bottom. Branch bodies get their own nested builder, which is how the graph gets its
/// structure without the caller handling node ids.
/// </remarks>
public interface IWorkflowBuilder<TData>
{
    /// <summary>
    /// Appends an activity. Identical to <see cref="Then{TActivity}"/> — it exists so a definition
    /// can open with the word that reads correctly.
    /// </summary>
    IWorkflowBuilder<TData> StartWith<TActivity>() where TActivity : IActivity<TData>;

    /// <summary>Appends an activity to the current sequence.</summary>
    IWorkflowBuilder<TData> Then<TActivity>() where TActivity : IActivity<TData>;

    /// <summary>
    /// Branches on a predicate over the data document.
    /// </summary>
    /// <remarks>
    /// <b>The predicate must be pure.</b> It is evaluated at execution time against the persisted
    /// document, and may be re-evaluated after a rehydration, so it must depend on nothing but
    /// <typeparamref name="TData"/>. Taking an <see cref="Expression{TDelegate}"/> rather than a
    /// delegate is what lets the exported shape show the condition instead of an opaque box.
    /// </remarks>
    IWorkflowBuilder<TData> If(
        Expression<Func<TData, bool>> condition,
        Action<IWorkflowBuilder<TData>> then,
        Action<IWorkflowBuilder<TData>>? otherwise = null);

    /// <summary>
    /// Runs branches concurrently, each as its own chain of jobs, and continues once all complete.
    /// </summary>
    /// <remarks>
    /// Branches should write disjoint regions of the document: each branch checkpoint merges under
    /// optimistic concurrency, and a conflict retries the merge rather than the activity (§6.2).
    /// </remarks>
    IWorkflowBuilder<TData> Parallel(params Action<IWorkflowBuilder<TData>>[] branches);

    /// <summary>
    /// A sequence whose completed steps are undone in reverse order if a later one fails past its
    /// retry policy (§6.4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Compensation is triggered by <em>exhausted retries</em>, not by the first exception: a step
    /// that fails transiently and then succeeds has not failed, and unwinding it would be wrong.
    /// </para>
    /// <para>
    /// Each compensation runs as its own durable job with its own retry policy. If a compensation
    /// itself fails past its retries the instance is left <c>Suspended</c> rather than forced to a
    /// terminal state — a half-undone saga is exactly the case where an operator should look before
    /// anything else happens.
    /// </para>
    /// </remarks>
    IWorkflowBuilder<TData> Saga(Action<ISagaBuilder<TData>> steps);

    /// <summary>Runs <paramref name="body"/> once per item of a collection selected from the document.</summary>
    IWorkflowBuilder<TData> ForEach<TItem>(
        Expression<Func<TData, IEnumerable<TItem>>> collection,
        Action<IWorkflowBuilder<TData>> body);

    /// <summary>
    /// Suspends for <paramref name="duration"/>, as a Layer 1 scheduled job carrying a resume token.
    /// </summary>
    /// <remarks>
    /// Durable by construction: the wait survives process restarts because it is a scheduled job,
    /// not a timer in memory. A seven-day delay fires seven days later even across deploys.
    /// </remarks>
    IWorkflowBuilder<TData> Delay(TimeSpan duration);

    /// <summary>
    /// Suspends until a correlated signal arrives, binding its payload into the document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A waiting instance holds <em>no job</em> — only a bookmark — so a workflow parked for a month
    /// costs nothing but a row (§6.3).
    /// </para>
    /// <para>
    /// The payload type is declared here and the sender uses the matching typed overload, so shape
    /// mismatches are compile-time errors. The wire format stays plain JSON, which keeps webhook and
    /// cross-language senders possible.
    /// </para>
    /// </remarks>
    /// <param name="name">Signal name; matched with <paramref name="correlate"/>.</param>
    /// <param name="correlate">Pure selector of the correlation id from the document.</param>
    /// <param name="bind">Applies the payload to the document when the signal arrives.</param>
    /// <param name="timeout">Optional: after this, the wait gives up and the sequence continues.</param>
    IWorkflowBuilder<TData> WaitForSignal<TPayload>(
        string name,
        Expression<Func<TData, string>> correlate,
        Action<TData, TPayload> bind,
        TimeSpan? timeout = null);
}

/// <summary>The steps of a <see cref="IWorkflowBuilder{TData}.Saga"/>, each optionally undoable.</summary>
public interface ISagaBuilder<TData>
{
    /// <summary>Appends a step.</summary>
    ISagaBuilder<TData> Then<TActivity>() where TActivity : IActivity<TData>;

    /// <summary>
    /// Declares the activity that undoes the step just appended.
    /// </summary>
    /// <exception cref="InvalidOperationException">No step has been appended yet.</exception>
    ISagaBuilder<TData> CompensateWith<TActivity>() where TActivity : IActivity<TData>;

    /// <summary>
    /// Declares what the step just appended does when its retries are exhausted (§6.4).
    /// </summary>
    /// <remarks>
    /// Annotates the preceding step, exactly like <see cref="CompensateWith{TActivity}"/>. Without
    /// it a step unwinds the saga, which is the right default and the wrong one for a step whose
    /// earlier work should stand or whose failure needs a human first.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No step has been appended yet.</exception>
    ISagaBuilder<TData> OnFailure(StepFailurePolicy policy);

    /// <summary>
    /// Appends a saga inside this one (§11.29, §11.35).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A failure inside the nested saga unwinds the nested saga first, completely, and only then
    /// reports failure outward — so this saga's compensations run against the state its own steps
    /// actually left behind.
    /// </para>
    /// <para>
    /// <paramref name="policy"/> has no default because it decides what this saga can still promise.
    /// It answers the other direction: the nested saga committed, this one failed later, and either
    /// the nested work is undone with it or it stands.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The nested saga has no steps.</exception>
    ISagaBuilder<TData> Saga(Action<ISagaBuilder<TData>> steps, NestedSagaPolicy policy);
}
