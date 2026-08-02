# Conformance facts

> **Generated file — do not edit.** Rendered from the conformance suites by
> `scripts/generate-conformance-facts.ps1`. Change the suites, then run it.

A provider that passes these is a supported provider. There are **110** of them, and they are
the definition rather than a description of it — [Writing a provider](writing-a-provider.md)
explains what you implement, and this is what gets checked.

Each line is a test method name with its underscores replaced by spaces, in source order. Put
the underscores back to find one in `Millrace.Storage.Verification`, or to run it on its own.
5 of them are theories, so a run reports more cases than there are lines here.

Nothing on this page was written for it. If a sentence below is unclear, the fix belongs in the
method name it came from.

## Job storage — `JobStorageConformanceSuite` (64 facts)

### Checkpoints

- Checkpoint transition and enqueue all commit together
- A stale checkpoint revision rolls back the whole transition
- A checkpoint for a missing instance rolls back the whole transition
- A fence rejection leaves the instance untouched
- Concurrent branch checkpoints have exactly one winner
- A transition without a checkpoint still behaves as before

### Continuations

- Parent success activates direct awaiting children only
- Parent death cancels the transitive awaiting closure and releases keys
- Active children shield their descendants from the cancel cascade
- Awaiting insert after parent succeeded is fixed up to enqueued
- Awaiting insert after parent death is fixed up to cancelled
- Awaiting insert after parent cancellation is fixed up to cancelled
- Awaiting insert with missing parent throws and rolls back the batch
- Awaiting inserts racing parent terminal apply never strand a child *(theory)*

### Idempotency keys

- Duplicate active key is a noop returning the existing id
- Concurrent same key enqueues yield exactly one job
- Key uniqueness is scoped per tenant with null as its own scope
- Terminal transition frees the key but retains the field
- Enqueue racing terminal release always returns a valid id
- Apply enqueue insert with duplicate active key is skipped as noop
- Terminal key release is visible to the same transitions enqueue inserts

### Batches

- Enqueue returns effective ids positionally
- Enqueue batch with duplicate job id throws and persists nothing

### Cancellation

- TryCancel pre active states cancel with cascade and key release *(theory)*
- TryCancel processing sets flag only and never blocks the fence
- TryCancel racing fenced apply yields exactly one terminal outcome
- TryCancel racing activation and claim yields exactly one owner
- TryCancel terminal and unknown jobs return false without mutation

### Claims and leases

- Claim is exclusive under contention
- Claim sets processing worker lease and increments attempt
- Claim returns at most max count
- Claim only returns requested queues
- Unexpired lease blocks reclaim
- Expired lease is reclaimable and increments attempt
- Claim order is priority desc then fifo across queue union
- Scheduled failed and awaiting jobs are never claimable directly

### Lease renewal

- Renew extends lease beyond original expiry
- Renew resurrects expired but unreclaimed lease
- Renewal racing reclaim has exactly one owner
- GetJob roundtrips every field
- Renew excludes jobs reclaimed by another worker

### The apply fence

- Apply with wrong worker is rejected without changes
- Apply with wrong attempt is rejected without changes
- Apply zombie versus new owner exactly one wins
- Apply succeeded sets terminal fields
- Run now makes a retrying job claimable without spending retry budget
- Run now refuses anything not awaiting a retry *(theory)*
- Run now on an unknown job reports false
- Apply failed schedules retry and activation makes it claimable
- Apply release returns job to queue without consuming retry budget
- Apply is all or nothing when an enqueue insert fails
- Apply enqueue inserts commit atomically with the transition

### Due activation

- Activate moves only due jobs oldest first respecting batch size
- Concurrent activation activates each job exactly once

### Recurring

- Recurring upsert roundtrips all fields
- Recurring upsert with same cron preserves next fire time
- Recurring upsert with changed cron takes the records next fire time
- GetDueRecurring returns due only within batch limit
- Activation breaks due time ties by enqueue order
- TryFire cas has exactly one winner and one enqueued job
- TryFire with stale expected time returns false and inserts nothing
- TryFire unknown id returns false
- Remove recurring removes the definition
- Same cron upsert racing fire never rewinds next fire time

## Workflow storage — `WorkflowStorageConformanceSuite` (11 facts)

- Create and get roundtrip with revision one
- Create normalizes revision to one regardless of input
- Duplicate create throws concurrency exception
- Update with matching revision increments it
- Update with stale revision throws and changes nothing
- Update of missing instance throws concurrency exception
- Concurrent updates have exactly one winner
- Consume with no match returns null
- Consume is at most once under contention
- Consume returns the oldest matching bookmark
- Consume breaks created at ties by id

## Monitoring read model — `MonitoringConformanceSuite` (35 facts)

- Query orders newest first
- Paging walks every row exactly once
- Last page reports a null cursor
- Empty result has no items and no cursor
- Paging is stable when rows change state underneath
- An undecodable cursor is rejected rather than restarting *(theory)*
- Limit is clamped never rejected
- State filter selects only those states
- Empty state list means any state
- Queue and created range filters combine with and
- Created range is lower inclusive and upper exclusive
- Tenant filter distinguishes any from untenanted
- Job details returns null for an unknown id
- Job details carries the payload the summary omits
- Interruptions separate infrastructure churn from recorded failures
- Statistics report every state including zeroes
- Statistics respect the tenant filter
- Statistics count recurring definitions and overdue ones
- Recurring definitions are ordered soonest first
- A job that succeeds first time records no attempt history
- A failed attempt is recorded with its error and worker
- An expired lease records an interruption rather than a failure
- A fenced release records an interruption
- Attempt history is newest first and capped without distorting the counters
- A definition that has never fired reports no last outcome
- The last outcome is the state of the most recently created fired job
- A running occurrence outranks an earlier success
- The link to a definition survives the definition being removed
- Recurring paging walks every definition exactly once
- Recurring ties on fire time break by id
- Recurring filters by queue and tenant and clamps the limit
- Recurring rejects an undecodable cursor *(theory)*
- Recurring summary carries schedule fields and no outcome
- Instance queries page and filter like job queries
- Instance version filter requires a definition id
