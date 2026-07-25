// Mirrors the frozen v1 contract (ARCHITECTURE.md §4.1, §11.12). Hand-written rather than
// generated: the surface is small, and a generator would be another build-time dependency in a
// package whose whole promise is that consumers install nothing.

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
 * A page of results. There is deliberately no total and no page count — §11.12 removed them
 * because counting a filtered, continuously changing job table is the expensive part of the
 * query. Consequently no view here offers page numbers.
 */
export interface Page<T> {
  items: T[]
  /** Opaque; round-trip it unmodified. Null means this is the last page. */
  nextCursor: string | null
}

export interface JobStatistics {
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
  /** When it last fired — not what happened. No outcome is stored; see issue #61. */
  lastFireTime: string | null
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
