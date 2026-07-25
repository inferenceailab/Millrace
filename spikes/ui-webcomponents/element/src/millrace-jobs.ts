import { LitElement, html, nothing, type TemplateResult } from 'lit'
import { customElement, property, state } from 'lit/decorators.js'
import { api, setApiBase, type JobQuery } from '../../../../src/ui-shared/api'
import { jobStates, type JobState, type JobSummary, type Page } from '../../../../src/ui-shared/contract'
import { formatTime } from '../../../../src/ui-shared/format'
import { jobsStyles } from './styles'

/**
 * An extra column a host can add without forking the element.
 *
 * `render` is a function, which is the crux of the Blazor finding: a C# host cannot supply one
 * without writing JavaScript, so this extension point is open to React and Angular and closed to
 * Blazor. See the spike README.
 */
export interface JobColumn {
  header: string
  render: (job: JobSummary) => string
  numeric?: boolean
}

/**
 * The jobs table as one custom element: filters, cursor paging, row selection.
 *
 * Deliberately the *same* view the React and Angular UIs already render, so the comparison is like
 * for like and the question is only what the packaging costs.
 */
@customElement('millrace-jobs')
export class MillraceJobs extends LitElement {
  static override styles = [jobsStyles]

  /**
   * Where the dashboard API is mounted, e.g. `/millrace/api/v1`.
   *
   * FINDING: the shipping UIs derive this from their own URL, because they are always served from
   * `{prefix}/ui`. An element embedded in someone else's application has no such guarantee — its
   * URL is the host's — so the base has to become explicit configuration the moment the UI stops
   * owning the page. This one attribute is the whole cost, but it is not optional.
   */
  @property({ type: String })
  base = ''

  /** Attribute, so a plain HTML or Blazor host can set it without touching JavaScript. */
  @property({ type: String, attribute: 'job-state' })
  jobState: JobState | '' = ''

  @property({ type: String })
  queue = ''

  @property({ type: Number, attribute: 'page-size' })
  pageSize = 25

  /** Property only — see {@link JobColumn}. */
  @property({ attribute: false })
  extraColumns: JobColumn[] = []

  @state() private page?: Page<JobSummary>
  @state() private error?: string
  @state() private loading = true
  @state() private cursors: (string | null)[] = [null]

  override connectedCallback(): void {
    super.connectedCallback()
    void this.load()
  }

  override willUpdate(changed: Map<string, unknown>): void {
    // A filter change invalidates the cursors already visited: they describe the previous query.
    if (changed.has('jobState') || changed.has('queue')) {
      this.cursors = [null]
      void this.load()
    }
  }

  private async load(): Promise<void> {
    if (this.base) setApiBase(this.base)
    this.loading = true
    this.error = undefined
    const query: JobQuery = {
      state: this.jobState ? [this.jobState] : undefined,
      queue: this.queue || undefined,
      cursor: this.cursors.at(-1) ?? null,
      limit: this.pageSize,
    }

    try {
      this.page = await api.jobs(query)
    } catch (error: unknown) {
      this.error = error instanceof Error ? error.message : String(error)
    } finally {
      this.loading = false
    }
  }

  /**
   * Row selection leaves as a composed CustomEvent rather than a link.
   *
   * The element cannot know the host's routing — a React hash route, an Angular router link, a
   * Blazor `NavigationManager` — so it reports what happened and the host decides. `composed: true`
   * is what lets the event cross the shadow boundary at all.
   */
  private select(job: JobSummary): void {
    this.dispatchEvent(
      new CustomEvent('job-select', { detail: { id: job.id }, bubbles: true, composed: true }),
    )
  }

  private go(cursor: string | null, back: boolean): void {
    this.cursors = back ? this.cursors.slice(0, -1) : [...this.cursors, cursor]
    void this.load()
  }

  override render(): TemplateResult {
    return html`
      <div class="controls">
        <div class="field">
          <label for="state">State</label>
          <select
            id="state"
            .value=${this.jobState}
            @change=${(e: Event) => {
              this.jobState = (e.target as HTMLSelectElement).value as JobState | ''
            }}
          >
            <option value="">Any</option>
            ${jobStates.map((s) => html`<option value=${s}>${s}</option>`)}
          </select>
        </div>
        <div class="field">
          <label for="queue">Queue</label>
          <input
            id="queue"
            type="text"
            placeholder="Any"
            .value=${this.queue}
            @input=${(e: Event) => {
              this.queue = (e.target as HTMLInputElement).value
            }}
          />
        </div>
      </div>

      ${this.loading ? html`<p class="muted">Loading…</p>` : nothing}
      ${this.error
        ? html`<div class="notice error"><strong>Request failed.</strong> ${this.error}</div>`
        : nothing}
      ${this.page ? this.table(this.page) : nothing}
    `
  }

  private table(page: Page<JobSummary>): TemplateResult {
    return html`
      <div class="table-scroll">
        <table>
          <thead>
            <tr>
              <th>State</th>
              <th>Job</th>
              <th>Queue</th>
              <th>Created</th>
              <th class="num">Attempts</th>
              ${this.extraColumns.map(
                (column) => html`<th class=${column.numeric ? 'num' : ''}>${column.header}</th>`,
              )}
            </tr>
          </thead>
          <tbody>
            ${page.items.map(
              (job) => html`
                <tr>
                  <td><span class="chip" data-state=${job.state}>${job.state}</span></td>
                  <td>
                    <a href="#" @click=${(e: Event) => (e.preventDefault(), this.select(job))}>
                      ${job.methodName}
                    </a>
                    <div class="muted mono">${job.typeName}</div>
                  </td>
                  <td>${job.queue}</td>
                  <td>${formatTime(job.createdAt)}</td>
                  <td class="num">${job.attempt}</td>
                  ${this.extraColumns.map(
                    (column) =>
                      html`<td class=${column.numeric ? 'num' : ''}>${column.render(job)}</td>`,
                  )}
                </tr>
              `,
            )}
            ${page.items.length === 0
              ? html`<tr>
                  <td colspan=${5 + this.extraColumns.length} class="muted">No jobs match.</td>
                </tr>`
              : nothing}
          </tbody>
        </table>
      </div>
      <div class="pager">
        <button
          type="button"
          ?disabled=${this.cursors.length <= 1}
          @click=${() => this.go(null, true)}
        >
          ← Previous
        </button>
        <button
          type="button"
          ?disabled=${!page.nextCursor}
          @click=${() => this.go(page.nextCursor, false)}
        >
          Next →
        </button>
        <span>${page.items.length} row${page.items.length === 1 ? '' : 's'} on this page</span>
      </div>
    `
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'millrace-jobs': MillraceJobs
  }
}
