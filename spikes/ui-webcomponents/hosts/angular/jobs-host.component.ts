import {
  ChangeDetectionStrategy,
  Component,
  CUSTOM_ELEMENTS_SCHEMA,
  signal,
} from '@angular/core'
import '../../element/src/millrace-jobs'

/**
 * Hosting `<millrace-jobs>` in Angular 22. THIS FILE IS THE MEASUREMENT.
 *
 * Everything an Angular consumer must write is here: one `CUSTOM_ELEMENTS_SCHEMA` line, and the
 * event needs no glue at all — Angular's `(event)` binding already works for any DOM event,
 * CustomEvents included. That makes Angular the cheapest of the three hosts, and Blazor the
 * dearest by a wide margin.
 */
@Component({
  selector: 'mr-jobs-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  // GLUE, in its entirety: without this Angular errors on the unknown element. It is also a blunt
  // instrument — it disables unknown-element checking for the whole template, not just this tag.
  schemas: [CUSTOM_ELEMENTS_SCHEMA],
  template: `
    <div class="layout">
      <header class="masthead">
        <h1>Millrace</h1>
        <span class="sub">Angular host — web-component spike (#86)</span>
      </header>

      <div class="controls">
        <div class="field">
          <label for="host-state">State (set from Angular)</label>
          <select id="host-state" (change)="setState($event)">
            <option value="">Any</option>
            <option value="Succeeded">Succeeded</option>
            <option value="Dead">Dead</option>
          </select>
        </div>
      </div>

      <!--
        [extraColumns] is a property binding carrying render functions — the extension point that is
        open here and closed to Blazor. (job-select) needs no registration.
      -->
      <millrace-jobs
        base="/millrace/api/v1"
        [attr.job-state]="state()"
        [attr.page-size]="10"
        [extraColumns]="extraColumns"
        (job-select)="onSelect($event)"
      ></millrace-jobs>

      @if (selected(); as id) {
        <p>Selected job: <code>{{ id }}</code> — routed by Angular, not by the element.</p>
      }
    </div>
  `,
})
export class JobsHost {
  protected readonly state = signal('')
  protected readonly selected = signal<string | null>(null)

  protected readonly extraColumns = [
    { header: 'Failures', numeric: true, render: (job: { failures: number }) => String(job.failures) },
  ]

  protected setState(event: Event): void {
    this.state.set((event.target as HTMLSelectElement).value)
  }

  protected onSelect(event: Event): void {
    this.selected.set((event as CustomEvent<{ id: string }>).detail.id)
  }
}
