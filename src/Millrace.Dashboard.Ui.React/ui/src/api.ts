import type {
  JobDetails,
  JobStatistics,
  JobSummary,
  Page,
  RecurringSummary,
  WorkflowInstanceSummary,
} from './types'

/**
 * The API base, derived from where this bundle is being served.
 *
 * The consumer chooses the mount prefix at runtime (`MapMillraceDashboard("/ops")`), so it cannot
 * be baked in at build time. The bundle always lives at `{prefix}/ui`, and the API at
 * `{prefix}/api/v1`, so the prefix is recoverable from our own path.
 */
export function apiBase(): string {
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
    const detail = await response.text().catch(() => '')
    throw new ApiError(detail || `Request failed with ${response.status}`, response.status)
  }

  return (await response.json()) as T
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

export const api = {
  statistics: (scope: TenantScope = {}) => get<JobStatistics>('/statistics', { ...scope }),

  jobs: (query: {
    state?: string[]
    queue?: string
    cursor?: string | null
    limit?: number
  } & TenantScope) => get<Page<JobSummary>>('/jobs', query),

  job: (id: string) => get<JobDetails>(`/jobs/${id}`),

  recurring: (query: { queue?: string; cursor?: string | null; limit?: number } & TenantScope) =>
    get<Page<RecurringSummary>>('/recurring', query),

  instances: (query: {
    state?: string[]
    definitionId?: string
    cursor?: string | null
    limit?: number
  } & TenantScope) => get<Page<WorkflowInstanceSummary>>('/instances', query),
}
