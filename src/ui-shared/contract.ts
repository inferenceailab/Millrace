// The frozen v1 dashboard contract (ARCHITECTURE.md §4.1, §11.12), as TypeScript.
//
// Shared by every JavaScript UI package. §11.4 committed to "features are designed once in the
// contract, rendered three times" — a per-UI copy of these types is that commitment broken on the
// first line, because the copies drift and the UIs stop being clients of one contract.
//
// Hand-written rather than generated: the surface is small, and a generator would be another
// build-time dependency in packages whose whole promise is that consumers install nothing.

export type JobState =
  | 'Scheduled'
  | 'Enqueued'
  | 'Processing'
  | 'Succeeded'
  | 'Failed'
  | 'Dead'
  | 'Cancelled'
  | 'Awaiting'

export const jobStates: JobState[] = [
  'Scheduled',
  'Enqueued',
  'Processing',
  'Succeeded',
  'Failed',
  'Dead',
  'Cancelled',
  'Awaiting',
]

export type WorkflowInstanceState =
  | 'Running'
  | 'Suspended'
  | 'Completed'
  | 'Failed'
  | 'Compensated'
  | 'Cancelled'

/**
 * Mirrors `JobStateExtensions.IsTerminal`. Note that `Failed` is deliberately not among them: it
 * means "waiting to retry", not "finished".
 */
export const terminalJobStates: JobState[] = ['Succeeded', 'Dead', 'Cancelled']

/** A terminal job cannot be cancelled — the API would answer 404 and say nothing useful. */
export function isCancellable(state: JobState): boolean {
  return !terminalJobStates.includes(state)
}

/**
 * Only a job waiting out its retry backoff can be brought forward. `Scheduled` looks similar and is
 * not: its due time is the caller's intent rather than a backoff (§11.32).
 */
export function isRunnableNow(state: JobState): boolean {
  return state === 'Failed'
}

/**
 * Requeue is refused for work still in flight — that is what cancel is for. `Failed` counts as
 * finished here even though it is not terminal, because `RequeueAsync` accepts it (§11.18).
 */
export function isRequeueable(state: JobState): boolean {
  return terminalJobStates.includes(state) || state === 'Failed'
}

/**
 * A page of results. There is deliberately no total and no page count — §11.12 removed them because
 * counting a filtered, continuously changing job table is the expensive part of the query.
 * Consequently no view offers page numbers.
 */
export interface Page<T> {
  items: T[]
  /** Opaque; round-trip it unmodified. Null means this is the last page. */
  nextCursor: string | null
}

export interface JobStatistics {
  /** Every state is present: the conformance kit requires the key even when the count is zero. */
  jobsByState: Record<JobState, number>
  enqueuedByQueue: Record<string, number>
  instancesByState: Record<WorkflowInstanceState, number>
  recurringDefinitions: number
  overdueRecurringDefinitions: number
}

export interface JobSummary {
  id: string
  queue: string
  state: JobState
  typeName: string
  methodName: string
  priority: number
  createdAt: string
  dueAt: string | null
  finishedAt: string | null
  attempt: number
  failures: number
  /** Derived server-side as attempt − failures: executions killed by infrastructure, not by code. */
  interruptions: number
  tenantId: string | null
  workerId: string | null
}

export interface JobInvocation {
  typeName: string
  methodName: string
  parameterTypes: string[]
  argumentsJson: (string | null)[]
}

export type JobAttemptOutcome = 'Failed' | 'Interrupted'

/**
 * One execution that did not succeed. Successful attempts are deliberately not recorded, so an
 * empty list means "nothing has gone wrong", not "no history kept".
 */
export interface JobAttempt {
  attempt: number
  outcome: JobAttemptOutcome
  /** When the attempt ended. Start times are not stored. */
  recordedAt: string
  workerId: string | null
  /** Always null for an interruption — an execution that vanished had nothing to report. */
  error: string | null
}

export interface JobDetails {
  summary: JobSummary
  invocation: JobInvocation
  retry: unknown
  idempotencyKey: string | null
  parentId: string | null
  lastError: string | null
  leaseUntil: string | null
  cancelRequested: boolean
  workflowInstanceId: string | null
  activityNodeId: string | null
  /** Failed and interrupted executions, newest first. Bounded per job. */
  attempts: JobAttempt[]
}

export interface RecurringSummary {
  id: string
  cron: string
  queue: string
  typeName: string
  methodName: string
  priority: number
  tenantId: string | null
  nextFireTime: string
  /** When it last fired. What happened is `lastOutcome`. */
  lastFireTime: string | null
  /**
   * What became of the most recently *created* job this definition produced, or null if it has
   * produced none. Creation order, not completion: a run still going reads `Processing` rather than
   * showing last night's success.
   */
  lastOutcome: JobState | null
  /** The job behind `lastOutcome`, so the view can link to its error. */
  lastJobId: string | null
  createdAt: string
  updatedAt: string
}

export interface WorkflowInstanceSummary {
  id: string
  definitionId: string
  definitionVersion: number
  state: WorkflowInstanceState
  tenantId: string | null
  createdAt: string
  updatedAt: string
  revision: number
}

/**
 * `GET /info`. The field is `apiVersion`, not `version` — this type was written from memory in #88
 * and never checked against the wire, so the header rendered `undefined`. The raw-JSON tests added
 * with §11.24 exist so the next mismatch fails a build instead of rendering blank.
 */
export interface DashboardInfo {
  apiVersion: string
  storageProvider: string
}

/** The new job's id, returned by requeue. */
export interface RequeuedJob {
  id: string
}
