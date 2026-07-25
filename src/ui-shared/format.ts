// Presentation helpers with no framework in them, so every JavaScript UI formats a timestamp the
// same way. Two dashboards rendering the same instant differently is a small bug that is very
// confusing to hit.

/** UTC, to the second, always — job times are UTC and a local rendering invites misreading. */
export function formatTime(value: string | null | undefined): string {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.valueOf())
    ? '—'
    : date.toISOString().replace('T', ' ').slice(0, 19) + 'Z'
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

export function isOverdue(nextFireTime: string): boolean {
  return new Date(nextFireTime).getTime() < Date.now()
}

/** "overdue by 3h" or "in 20m" — the schedule view's one piece of derived text. */
export function dueText(nextFireTime: string): string {
  const relative = relativeToNow(nextFireTime)
  return isOverdue(nextFireTime) ? `overdue by ${relative.replace(' ago', '')}` : relative
}

export function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}
