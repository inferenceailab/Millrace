import { useState } from 'react'
import { api, ApiError } from '../../../ui-shared/api'
import { isCancellable, isRequeueable, jobStates, type JobState } from '../../../ui-shared/contract'
import {
  ErrorNotice,
  Loading,
  Pager,
  StateChip,
  dueText,
  errorMessage,
  formatTime,
  isOverdue,
  useAsync,
  useCursorStack,
} from './shared'

/** Overview — every figure answers one question, so these are tiles, not a chart. */
export function Overview() {
  const { data, error, loading } = useAsync(() => api.statistics(), [])

  if (loading) return <Loading />
  if (error) return <ErrorNotice message={error} />
  if (!data) return null

  const queues = Object.entries(data.enqueuedByQueue).sort((a, b) => b[1] - a[1])

  return (
    <>
      <section>
        <h2>Jobs by state</h2>
        <div className="tiles">
          {jobStates.map((state) => {
            const value = data.jobsByState[state] ?? 0
            return (
              <div className={`tile${value === 0 ? ' is-zero' : ''}`} key={state}>
                <div className="label">
                  <StateChip state={state} />
                </div>
                <div className="value">{value.toLocaleString()}</div>
              </div>
            )
          })}
        </div>
      </section>

      <section>
        <h2>Schedules</h2>
        <div className="tiles">
          <div className="tile">
            <div className="label">Recurring</div>
            <div className="value">{data.recurringDefinitions.toLocaleString()}</div>
          </div>
          <div className={`tile${data.overdueRecurringDefinitions === 0 ? ' is-zero' : ''}`}>
            <div className="label">Overdue</div>
            <div className={`value${data.overdueRecurringDefinitions > 0 ? ' overdue' : ''}`}>
              {data.overdueRecurringDefinitions.toLocaleString()}
            </div>
          </div>
        </div>
        {data.overdueRecurringDefinitions > 0 && (
          <div className="notice error">
            <strong>{data.overdueRecurringDefinitions} schedule(s) are overdue.</strong> Either the
            scheduler is behind, or no node is running the scheduler role.
          </div>
        )}
      </section>

      <section>
        <h2>Queue depth</h2>
        {queues.length === 0 ? (
          <p className="muted">Nothing claimable.</p>
        ) : (
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>Queue</th>
                  <th className="num">Enqueued</th>
                </tr>
              </thead>
              <tbody>
                {queues.map(([queue, depth]) => (
                  <tr key={queue}>
                    <td>{queue}</td>
                    <td className="num">{depth.toLocaleString()}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </>
  )
}

export function Jobs() {
  const [state, setState] = useState<JobState | ''>('')
  const [queue, setQueue] = useState('')
  const filterKey = `${state}|${queue}`
  const page = useCursorStack(filterKey)

  const { data, error, loading } = useAsync(
    () =>
      api.jobs({
        state: state ? [state] : undefined,
        queue: queue || undefined,
        cursor: page.cursor,
        limit: 25,
      }),
    [state, queue, page.cursor],
  )

  return (
    <>
      <div className="controls">
        <div className="field">
          <label htmlFor="job-state">State</label>
          <select
            id="job-state"
            value={state}
            onChange={(e) => setState(e.target.value as JobState | '')}
          >
            <option value="">Any</option>
            {jobStates.map((s) => (
              <option key={s} value={s}>
                {s}
              </option>
            ))}
          </select>
        </div>
        <div className="field">
          <label htmlFor="job-queue">Queue</label>
          <input
            id="job-queue"
            type="text"
            placeholder="Any"
            value={queue}
            onChange={(e) => setQueue(e.target.value)}
          />
        </div>
      </div>

      {loading && <Loading />}
      {error && <ErrorNotice message={error} />}
      {data && (
        <>
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>State</th>
                  <th>Job</th>
                  <th>Queue</th>
                  <th>Created</th>
                  <th className="num">Attempts</th>
                  <th className="num">Failures</th>
                  <th className="num">Interrupted</th>
                </tr>
              </thead>
              <tbody>
                {data.items.map((job) => (
                  <tr key={job.id}>
                    <td>
                      <StateChip state={job.state} />
                    </td>
                    <td>
                      <a href={`#/jobs/${job.id}`}>{job.methodName}</a>
                      <div className="muted mono">{job.typeName}</div>
                    </td>
                    <td>{job.queue}</td>
                    <td>{formatTime(job.createdAt)}</td>
                    <td className="num">{job.attempt}</td>
                    <td className="num">{job.failures}</td>
                    <td className="num">{job.interruptions}</td>
                  </tr>
                ))}
                {data.items.length === 0 && (
                  <tr>
                    <td colSpan={7} className="muted">
                      No jobs match.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
          <Pager
            nextCursor={data.nextCursor}
            canGoBack={page.canGoBack}
            onNext={() => data.nextCursor && page.next(data.nextCursor)}
            onBack={page.back}
            count={data.items.length}
          />
        </>
      )}
    </>
  )
}

export function JobDetail({ id }: { id: string }) {
  const [nonce, setNonce] = useState(0)
  const { data, error, loading } = useAsync(() => api.job(id), [id, nonce])
  const [action, setAction] = useState<{ message?: string; error?: string; busy: boolean }>({
    busy: false,
  })

  async function run(act: () => Promise<string>) {
    setAction({ busy: true })
    try {
      setAction({ busy: false, message: await act() })
      setNonce((n) => n + 1)
    } catch (e: unknown) {
      setAction({ busy: false, error: errorMessage(e) })
    }
  }

  if (loading) return <Loading />
  if (error) return <ErrorNotice message={error} />
  if (!data) return null

  const s = data.summary
  const cancellable = isCancellable(s.state)
  const requeueable = isRequeueable(s.state)

  return (
    <>
      <p>
        <a href="#/jobs">← All jobs</a>
      </p>
      <h2>
        {s.methodName} <StateChip state={s.state} />
      </h2>

      <div className="controls">
        <button
          type="button"
          disabled={action.busy || !cancellable}
          onClick={() =>
            run(async () => {
              await api.cancelJob(id)
              // Deliberately not "cancelled": a running job is asked to stop, and the answer does
              // not promise the work did not happen.
              return 'Cancellation requested.'
            })
          }
        >
          Cancel
        </button>
        <button
          type="button"
          disabled={action.busy || !requeueable}
          onClick={() =>
            run(async () => {
              const requeued = await api.requeueJob(id)
              window.location.hash = `#/jobs/${requeued.id}`
              return 'Requeued.'
            })
          }
        >
          Requeue
        </button>
        {action.message && <span className="muted">{action.message}</span>}
        {action.error && <span className="overdue">{action.error}</span>}
      </div>
      <p className="muted">
        {cancellable
          ? 'Cancelling a running job is cooperative — it is asked to stop, and one about to finish may still succeed.'
          : requeueable
            ? 'Requeue runs this work again as a new job. This record has finished and stays as it is; the new job links back to it.'
            : ''}
      </p>
      <dl className="detail-grid">
        <dt>Id</dt>
        <dd className="mono">{s.id}</dd>
        <dt>Type</dt>
        <dd className="mono">{s.typeName}</dd>
        <dt>Queue</dt>
        <dd>
          {s.queue} · priority {s.priority}
        </dd>
        <dt>Created</dt>
        <dd>{formatTime(s.createdAt)}</dd>
        <dt>Due</dt>
        <dd>{formatTime(s.dueAt)}</dd>
        <dt>Finished</dt>
        <dd>{formatTime(s.finishedAt)}</dd>
        <dt>Attempts</dt>
        <dd>
          {s.attempt} started · {s.failures} failed · {s.interruptions} interrupted
          <div className="muted">
            Interruptions are executions killed by infrastructure — crashes, deploys, lost leases —
            rather than by failing code. Per-attempt history is not stored, so there is no timeline.
          </div>
        </dd>
        <dt>Worker</dt>
        <dd>{s.workerId ?? '—'}</dd>
        <dt>Lease until</dt>
        <dd>{formatTime(data.leaseUntil)}</dd>
        <dt>Tenant</dt>
        <dd>{s.tenantId ?? '—'}</dd>
        <dt>Idempotency key</dt>
        <dd className="mono">{data.idempotencyKey ?? '—'}</dd>
        <dt>Cancel requested</dt>
        <dd>{data.cancelRequested ? 'yes' : 'no'}</dd>
        {data.parentId && (
          <>
            <dt>Continuation of</dt>
            <dd>
              <a href={`#/jobs/${data.parentId}`} className="mono">
                {data.parentId}
              </a>
            </dd>
          </>
        )}
      </dl>

      <h2>Arguments</h2>
      <pre>
        {data.invocation.parameterTypes.length === 0
          ? '(none)'
          : data.invocation.parameterTypes
              .map((type, i) => `${type}\n  ${data.invocation.argumentsJson[i] ?? 'null'}`)
              .join('\n\n')}
      </pre>

      {data.attempts.length > 0 && (
        <>
          <h2>Attempts</h2>
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th className="num">#</th>
                  <th>Outcome</th>
                  <th>Ended</th>
                  <th>Worker</th>
                  <th>Error</th>
                </tr>
              </thead>
              <tbody>
                {data.attempts.map((a) => (
                  <tr key={a.attempt}>
                    <td className="num">{a.attempt}</td>
                    <td>
                      <StateChip state={a.outcome === 'Failed' ? 'Failed' : 'Cancelled'} />
                      {a.outcome === 'Interrupted' && ' interrupted'}
                    </td>
                    <td>{formatTime(a.recordedAt)}</td>
                    <td className="mono">{a.workerId ?? '—'}</td>
                    <td>
                      {a.error ? (
                        <pre>{a.error}</pre>
                      ) : (
                        <span className="muted">no verdict recorded</span>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <p className="muted" style={{ marginTop: 12 }}>
            Only failed and interrupted executions are listed — a successful attempt records nothing,
            so a job that worked first time shows none. Bounded per job, while the counts above are
            exact.
          </p>
        </>
      )}

      {data.lastError && (
        <>
          <h2>Last error</h2>
          <pre>{data.lastError}</pre>
        </>
      )}
    </>
  )
}

export function Recurring() {
  const page = useCursorStack('recurring')
  const [nonce, setNonce] = useState(0)
  const { data, error, loading } = useAsync(
    () => api.recurring({ cursor: page.cursor, limit: 25 }),
    [page.cursor, nonce],
  )
  const [triggered, setTriggered] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function trigger(id: string) {
    setBusy(true)
    setTriggered(null)
    setActionError(null)
    try {
      await api.triggerRecurring(id)
      setTriggered(id)
      // The definition's own row does not change — NextFireTime is deliberately untouched — but the
      // job it enqueued is now real, so anything derived from the list should be re-read.
      setNonce((n) => n + 1)
    } catch (e: unknown) {
      setActionError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  if (loading) return <Loading />
  if (error) return <ErrorNotice message={error} />
  if (!data) return null

  return (
    <>
      {actionError && <ErrorNotice message={actionError} />}
      <div className="table-scroll">
        <table>
          <thead>
            <tr>
              <th>Id</th>
              <th>Cron (UTC)</th>
              <th>Queue</th>
              <th>Next fire</th>
              <th>Last fired</th>
              <th>Last outcome</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {data.items.map((definition) => {
              const overdue = isOverdue(definition.nextFireTime)
              return (
                <tr key={definition.id}>
                  <td>
                    {definition.id}
                    <div className="muted mono">{definition.methodName}</div>
                  </td>
                  <td className="mono">{definition.cron}</td>
                  <td>{definition.queue}</td>
                  <td className={overdue ? 'overdue' : undefined}>
                    {formatTime(definition.nextFireTime)}
                    <div className={overdue ? 'overdue' : 'muted'}>
                      {dueText(definition.nextFireTime)}
                    </div>
                  </td>
                  <td>{formatTime(definition.lastFireTime)}</td>
                  <td>
                    {definition.lastOutcome ? (
                      <a href={`#/jobs/${definition.lastJobId}`}>
                        <StateChip state={definition.lastOutcome} />
                      </a>
                    ) : (
                      <span className="muted">never run</span>
                    )}
                  </td>
                  <td>
                    <button type="button" disabled={busy} onClick={() => trigger(definition.id)}>
                      Run now
                    </button>
                    {triggered === definition.id && <div className="muted">Fired.</div>}
                  </td>
                </tr>
              )
            })}
            {data.items.length === 0 && (
              <tr>
                <td colSpan={7} className="muted">
                  No recurring definitions.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
      <Pager
        nextCursor={data.nextCursor}
        canGoBack={page.canGoBack}
        onNext={() => data.nextCursor && page.next(data.nextCursor)}
        onBack={page.back}
        count={data.items.length}
      />
      <p className="muted" style={{ marginTop: 12 }}>
        Running now adds an extra occurrence and leaves the schedule alone — the next fire time is
        unchanged. Last outcome is the most recently <em>created</em> run, so an occurrence still
        going shows as running rather than reporting the previous night's result.
      </p>
    </>
  )
}

/**
 * Sending a signal by hand.
 *
 * The payload is raw JSON rather than a form, because the workflow definition declares the payload
 * type and the engine binds on its side of the wire (§11.5) — the dashboard has no schema to render
 * a form from, and pretending otherwise would be a lie about what it knows.
 */
export function Signals() {
  const [name, setName] = useState('')
  const [correlationId, setCorrelationId] = useState('')
  const [payload, setPayload] = useState('')
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<string | null>(null)
  const [failure, setFailure] = useState<string | null>(null)

  // Validated here so a typo fails before it reaches a workflow, not inside one.
  let payloadError: string | null = null
  if (payload.trim().length > 0) {
    try {
      JSON.parse(payload)
    } catch (e: unknown) {
      payloadError = errorMessage(e)
    }
  }

  const ready = name.trim().length > 0 && correlationId.trim().length > 0 && payloadError === null

  async function send() {
    setBusy(true)
    setResult(null)
    setFailure(null)
    try {
      await api.sendSignal(name.trim(), correlationId.trim(), payload.trim())
      setResult('Delivered.')
    } catch (e: unknown) {
      setFailure(
        e instanceof ApiError && e.status === 404
          ? 'No instance is waiting on that name and correlation id.'
          : errorMessage(e),
      )
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <p className="muted">
        Delivers a signal to an instance waiting on this name and correlation id. Delivery is
        at-most-once, so "nothing was waiting" is a normal answer rather than a fault — a signal sent
        before the instance reaches its wait is simply lost.
      </p>

      <div className="controls">
        <div className="field">
          <label htmlFor="signal-name">Name</label>
          <input
            id="signal-name"
            type="text"
            placeholder="OrderApproved"
            value={name}
            onChange={(e) => setName(e.target.value)}
          />
        </div>
        <div className="field">
          <label htmlFor="signal-correlation">Correlation id</label>
          <input
            id="signal-correlation"
            type="text"
            placeholder="order-42"
            value={correlationId}
            onChange={(e) => setCorrelationId(e.target.value)}
          />
        </div>
        <button type="button" disabled={busy || !ready} onClick={send}>
          Send
        </button>
      </div>

      <div className="field">
        <label htmlFor="signal-payload">Payload (JSON, optional)</label>
        <textarea
          id="signal-payload"
          rows={6}
          spellCheck={false}
          style={{ fontFamily: 'ui-monospace, SFMono-Regular, Menlo, monospace', fontSize: 12 }}
          value={payload}
          onChange={(e) => setPayload(e.target.value)}
        />
      </div>

      {payloadError && (
        <div className="notice error">
          <strong>Payload is not valid JSON.</strong> {payloadError}
        </div>
      )}
      {result && <div className="notice">{result}</div>}
      {failure && (
        <div className="notice error">
          <strong>Not delivered.</strong> {failure}
        </div>
      )}
    </>
  )
}

export function Instances() {
  const [definitionId, setDefinitionId] = useState('')
  const page = useCursorStack(definitionId)
  const { data, error, loading } = useAsync(
    () => api.instances({ definitionId: definitionId || undefined, cursor: page.cursor, limit: 25 }),
    [definitionId, page.cursor],
  )

  return (
    <>
      <div className="controls">
        <div className="field">
          <label htmlFor="definition">Definition</label>
          <input
            id="definition"
            type="text"
            placeholder="Any"
            value={definitionId}
            onChange={(e) => setDefinitionId(e.target.value)}
          />
        </div>
      </div>

      {loading && <Loading />}
      {error && <ErrorNotice message={error} />}
      {data && (
        <>
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>State</th>
                  <th>Definition</th>
                  <th className="num">Version</th>
                  <th>Created</th>
                  <th>Last checkpoint</th>
                  <th className="num">Revision</th>
                </tr>
              </thead>
              <tbody>
                {data.items.map((instance) => (
                  <tr key={instance.id}>
                    <td>
                      <StateChip state={instance.state} />
                    </td>
                    <td>
                      {instance.definitionId}
                      <div className="muted mono">{instance.id}</div>
                    </td>
                    <td className="num">{instance.definitionVersion}</td>
                    <td>{formatTime(instance.createdAt)}</td>
                    <td>{formatTime(instance.updatedAt)}</td>
                    <td className="num">{instance.revision}</td>
                  </tr>
                ))}
                {data.items.length === 0 && (
                  <tr>
                    <td colSpan={6} className="muted">
                      No workflow instances. The engine lands in 0.3.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
          <Pager
            nextCursor={data.nextCursor}
            canGoBack={page.canGoBack}
            onNext={() => data.nextCursor && page.next(data.nextCursor)}
            onBack={page.back}
            count={data.items.length}
          />
        </>
      )}
    </>
  )
}
