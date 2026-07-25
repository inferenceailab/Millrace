import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { api } from '../../../../ui-shared/api';
import { jobStates, type JobState } from '../../../../ui-shared/contract';
import {
  asyncSignal,
  Chip,
  CursorStack,
  ErrorNotice,
  formatTime,
  Loading,
  Pager,
} from './shared';

@Component({
  selector: 'mr-jobs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Chip, ErrorNotice, Loading, Pager],
  template: `
    <div class="controls">
      <div class="field">
        <label for="job-state">State</label>
        <select id="job-state" [value]="state()" (change)="setState($event)">
          <option value="">Any</option>
          @for (s of states; track s) {
            <option [value]="s">{{ s }}</option>
          }
        </select>
      </div>
      <div class="field">
        <label for="job-queue">Queue</label>
        <input
          id="job-queue"
          type="text"
          placeholder="Any"
          [value]="queue()"
          (input)="setQueue($event)"
        />
      </div>
    </div>

    @if (result().loading) {
      <mr-loading />
    }
    @if (result().error; as error) {
      <mr-error [message]="error" />
    }
    @if (result().data; as data) {
      <div class="table-scroll">
        <table>
          <thead>
            <tr>
              <th>State</th>
              <th>Job</th>
              <th>Queue</th>
              <th>Created</th>
              <th class="num">Attempts</th>
              <th class="num">Failures</th>
              <th class="num">Interrupted</th>
            </tr>
          </thead>
          <tbody>
            @for (job of data.items; track job.id) {
              <tr>
                <td><mr-chip [state]="job.state" /></td>
                <td>
                  <a [href]="'#/jobs/' + job.id">{{ job.methodName }}</a>
                  <div class="muted mono">{{ job.typeName }}</div>
                </td>
                <td>{{ job.queue }}</td>
                <td>{{ time(job.createdAt) }}</td>
                <td class="num">{{ job.attempt }}</td>
                <td class="num">{{ job.failures }}</td>
                <td class="num">{{ job.interruptions }}</td>
              </tr>
            } @empty {
              <tr>
                <td colspan="7" class="muted">No jobs match.</td>
              </tr>
            }
          </tbody>
        </table>
      </div>
      <mr-pager
        [nextCursor]="data.nextCursor"
        [canGoBack]="page.canGoBack()"
        [count]="data.items.length"
        (next)="data.nextCursor && page.next(data.nextCursor)"
        (back)="page.back()"
      />
    }
  `,
})
export class Jobs {

  protected readonly states = jobStates;
  protected readonly state = signal<JobState | ''>('');
  protected readonly queue = signal('');
  protected readonly page = new CursorStack();
  protected readonly time = formatTime;

  protected readonly result = asyncSignal(() =>
    api.jobs({
      state: this.state() ? [this.state()] : undefined,
      queue: this.queue() || undefined,
      cursor: this.page.cursor(),
      limit: 25,
    }),
  ).state;

  protected setState(event: Event): void {
    this.state.set((event.target as HTMLSelectElement).value as JobState | '');
    this.page.reset();
  }

  protected setQueue(event: Event): void {
    this.queue.set((event.target as HTMLInputElement).value);
    this.page.reset();
  }
}
