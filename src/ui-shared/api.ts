import type {
  DashboardInfo,
  JobDetails,
  JobStatistics,
  JobSummary,
  Page,
  RecurringSummary,
  RequeuedJob,
  WorkflowInstanceSummary,
} from './contract'

/**
 * The client for the versioned REST contract, shared by every JavaScript UI package.
 *
 * Plain `fetch` and no framework: the dashboard makes a handful of same-origin GETs and POSTs, so
 * there is nothing here for React or Angular to contribute. Keeping it framework-free is what makes
 * it shareable, and sharing it is what stops one UI quietly falling behind the contract — which is
 * the failure this file exists to prevent, not a tidiness preference.
 */

/**
 * The API base, derived from where this bundle is being served.
 *
 * The consumer chooses the mount prefix at runtime (`MapMillraceDashboard("/ops")`), so it cannot be
 * baked in at build time. The bundle always lives at `{prefix}/ui`, and the API at `{prefix}/api/v1`,
 * so the prefix is recoverable from our own path.
 */
let explicitBase: string | null = null

/**
 * Overrides the derived base.
 *
 * Needed when the client is *not* running from the dashboard's own mount — a UI embedded in someone
 * else's application cannot infer the prefix from its own URL, because its URL is theirs. Found by
 * the web-component spike (#86), where the element hosted at `/` derived `/api/v1` and 404'd every
 * request.
 */
export function setApiBase(base: string): void {
  explicitBase = base.replace(/\/$/, '')
}

export function apiBase(): string {
  if (explicitBase !== null) return explicitBase

  const path = window.location.pathname
  const marker = path.indexOf('/ui')
  const prefix = marker >= 0 ? path.slice(0, marker) : ''
  return `${prefix}/api/v1`
}

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message)
  }
}

/**
 * The three-way tenant filter as the API expresses it: `untenanted=true` for the null scope,
 * `tenant=x` for one tenant, neither for no constraint.
 *
 * A type alias rather than an interface on purpose — only aliases get an implicit index signature,
 * which is what lets these query objects pass as `Record<string, unknown>` to the query builder.
 */
export type TenantScope = {
  tenant?: string
  untenanted?: boolean
}

export type JobQuery = TenantScope & {
  state?: string[]
  queue?: string
  cursor?: string | null
  limit?: number
}

export type RecurringQuery = TenantScope & {
  queue?: string
  cursor?: string | null
  limit?: number
}

export type InstanceQuery = TenantScope & {
  state?: string[]
  definitionId?: string
  cursor?: string | null
  limit?: number
}

async function get<T>(path: string, params?: Record<string, unknown>): Promise<T> {
  const url = new URL(`${apiBase()}${path}`, window.location.origin)
  for (const [key, value] of Object.entries(params ?? {})) {
    if (value === undefined || value === null || value === '') continue
    if (Array.isArray(value)) {
      for (const item of value) url.searchParams.append(key, String(item))
    } else {
      url.searchParams.set(key, String(value))
    }
  }

  const response = await fetch(url, { headers: { accept: 'application/json' } })
  if (!response.ok) {
    // 400 is a rejected cursor; 404 is either an unknown id or an unauthorized caller, since the
    // API declines to confirm its own existence.
    throw new ApiError(await detail(response), response.status)
  }

  return (await response.json()) as T
}

async function post(path: string, body?: string): Promise<unknown> {
  const response = await fetch(new URL(`${apiBase()}${path}`, window.location.origin), {
    method: 'POST',
    headers: body === undefined ? {} : { 'content-type': 'application/json' },
    body,
  })

  if (!response.ok) {
    throw new ApiError(await detail(response), response.status)
  }

  // Most management endpoints answer 200 with no body.
  const text = await response.text()
  return text ? (JSON.parse(text) as unknown) : null
}

async function detail(response: Response): Promise<string> {
  const text = await response.text().catch(() => '')
  return text || `Request failed with ${response.status}`
}

export const api = {
  // ----------------------------------------------------------------------- monitoring

  info: () => get<DashboardInfo>('/info'),

  statistics: (scope: TenantScope = {}) => get<JobStatistics>('/statistics', { ...scope }),

  jobs: (query: JobQuery) => get<Page<JobSummary>>('/jobs', query),

  job: (id: string) => get<JobDetails>(`/jobs/${id}`),

  recurring: (query: RecurringQuery) => get<Page<RecurringSummary>>('/recurring', query),

  instances: (query: InstanceQuery) => get<Page<WorkflowInstanceSummary>>('/instances', query),

  // ----------------------------------------------------------------------- management

  /**
   * Asks a job to stop. 200 is not a promise the work did not happen: a running job is cancelled
   * cooperatively, and one about to finish may still succeed.
   */
  cancelJob: (id: string) => post(`/jobs/${id}/cancel`).then(() => undefined),

  /**
   * Runs a job that is waiting out its retry backoff, now.
   *
   * Shortens the wait and nothing else — no retry budget is spent. 404 means the job is not
   * awaiting a retry, which is the ordinary answer for a stale button.
   */
  runJobNow: (id: string) => post(`/jobs/${id}/run-now`).then(() => undefined),

  /** Runs a finished job again as a new job. 409 means it has not finished — cancel it first. */
  requeueJob: (id: string) => post(`/jobs/${id}/requeue`) as Promise<RequeuedJob>,

  /** Fires a recurring definition now. An extra occurrence: the schedule is untouched. */
  triggerRecurring: (id: string) =>
    post(`/recurring/${encodeURIComponent(id)}/trigger`).then(() => undefined),

  /**
   * Delivers a signal to a waiting instance. Delivery is at-most-once, so a 404 means nothing was
   * waiting on that name and correlation id — which is a normal answer, not a fault.
   */
  /**
   * Moves an unwind that a failed compensation left suspended.
   *
   * 404 means there is nothing to recover — not suspended mid-unwind, or somebody already did it.
   * That is the ordinary answer for a stale button, so callers should re-read rather than alarm.
   */
  recoverCompensation: (instanceId: string, action: 'retry' | 'skip' | 'abandon') =>
    post(`/instances/${instanceId}/compensation/${action}`).then(() => undefined),

  sendSignal: (name: string, correlationId: string, payloadJson: string) =>
    post(
      `/signals/${encodeURIComponent(name)}/${encodeURIComponent(correlationId)}`,
      payloadJson,
    ).then(() => undefined),
}
