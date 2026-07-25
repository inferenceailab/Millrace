import { useEffect, useState } from 'react'

/** Hash routing: no router dependency, and it survives any mount prefix without configuration. */
export function useRoute(): string {
  const [route, setRoute] = useState(() => window.location.hash.slice(1) || '/')
  useEffect(() => {
    const onChange = () => setRoute(window.location.hash.slice(1) || '/')
    window.addEventListener('hashchange', onChange)
    return () => window.removeEventListener('hashchange', onChange)
  }, [])
  return route
}

export interface Async<T> {
  data?: T
  error?: string
  loading: boolean
}

/** Loads whenever `deps` change, discarding results from superseded requests. */
export function useAsync<T>(load: () => Promise<T>, deps: unknown[]): Async<T> {
  const [state, setState] = useState<Async<T>>({ loading: true })

  useEffect(() => {
    let live = true
    setState((prev) => ({ ...prev, loading: true, error: undefined }))
    load().then(
      (data) => live && setState({ data, loading: false }),
      (error: unknown) =>
        live && setState({ loading: false, error: error instanceof Error ? error.message : String(error) }),
    )
    return () => {
      live = false
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps)

  return state
}

export function StateChip({ state }: { state: string }) {
  // The dot carries colour, the text carries meaning — never colour alone.
  return (
    <span className="chip" data-state={state}>
      {state}
    </span>
  )
}

export function formatTime(value: string | null | undefined): string {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.valueOf()) ? '—' : date.toISOString().replace('T', ' ').slice(0, 19) + 'Z'
}

export function relativeToNow(value: string): string {
  const delta = new Date(value).getTime() - Date.now()
  const abs = Math.abs(delta)
  const minute = 60_000
  const hour = 3_600_000
  const day = 86_400_000

  let text: string
  if (abs < minute) text = `${Math.round(abs / 1000)}s`
  else if (abs < hour) text = `${Math.round(abs / minute)}m`
  else if (abs < day) text = `${Math.round(abs / hour)}h`
  else text = `${Math.round(abs / day)}d`

  return delta >= 0 ? `in ${text}` : `${text} ago`
}

export function Loading() {
  return <p className="muted">Loading…</p>
}

export function ErrorNotice({ message }: { message: string }) {
  return (
    <div className="notice error">
      <strong>Request failed.</strong> {message}
    </div>
  )
}

/**
 * Next/previous paging over opaque cursors.
 *
 * There is no page number and no total, because §11.12 does not ship one. "Previous" works by
 * keeping the cursors already visited on a stack rather than by arithmetic.
 */
export function useCursorStack(resetKey: string) {
  const [stack, setStack] = useState<(string | null)[]>([null])
  useEffect(() => setStack([null]), [resetKey])

  const cursor = stack[stack.length - 1] ?? null
  return {
    cursor,
    canGoBack: stack.length > 1,
    next: (nextCursor: string) => setStack((s) => [...s, nextCursor]),
    back: () => setStack((s) => (s.length > 1 ? s.slice(0, -1) : s)),
  }
}

export function Pager({
  nextCursor,
  canGoBack,
  onNext,
  onBack,
  count,
}: {
  nextCursor: string | null
  canGoBack: boolean
  onNext: () => void
  onBack: () => void
  count: number
}) {
  return (
    <div className="pager">
      <button type="button" onClick={onBack} disabled={!canGoBack}>
        ← Previous
      </button>
      <button type="button" onClick={onNext} disabled={!nextCursor}>
        Next →
      </button>
      <span>
        {count} row{count === 1 ? '' : 's'} on this page
      </span>
    </div>
  )
}
