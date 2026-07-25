import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { api } from '../../../../ui-shared/api';
import {
  asyncSignal,
  Chip,
  CursorStack,
  dueText,
  ErrorNotice,
  errorMessage,
  formatTime,
  isOverdue,
  Loading,
  Pager,
} from './shared';

@Component({
  selector: 'mr-recurring',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Chip, ErrorNotice, Loading, Pager],
  template: `
    @if (result().loading) {
      <mr-loading />
    }
    @if (result().error; as error) {
      <mr-error [message]="error" />
    }
    @if (actionError(); as error) {
      <mr-error [message]="error" />
    }
    @if (result().data; as data) {
      <div class="table-scroll">
        <table>
          <thead>
            <tr>
              <th>Id</th>
              <th>Cron (UTC)</th>
              <th>Queue</th>
              <th>Next fire</th>
              <th>Last fired</th>
              <th>Last outcome</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            @for (definition of data.items; track definition.id) {
              <tr>
                <td>
                  {{ definition.id }}
                  <div class="muted mono">{{ definition.methodName }}</div>
                </td>
                <td class="mono">{{ definition.cron }}</td>
                <td>{{ definition.queue }}</td>
                <td [class.overdue]="isOverdue(definition.nextFireTime)">
                  {{ time(definition.nextFireTime) }}
                  <div [class]="isOverdue(definition.nextFireTime) ? 'overdue' : 'muted'">
                    {{ dueText(definition.nextFireTime) }}
                  </div>
                </td>
                <td>{{ time(definition.lastFireTime) }}</td>
                <td>
                  @if (definition.lastOutcome; as outcome) {
                    <a [href]="'#/jobs/' + definition.lastJobId"><mr-chip [state]="outcome" /></a>
                  } @else {
                    <span class="muted">never run</span>
                  }
                </td>
                <td>
                  <button type="button" (click)="trigger(definition.id)" [disabled]="busy()">
                    Run now
                  </button>
                  @if (triggered() === definition.id) {
                    <div class="muted">Fired.</div>
                  }
                </td>
              </tr>
            } @empty {
              <tr>
                <td colspan="7" class="muted">No recurring definitions.</td>
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
      <p class="muted" style="margin-top: 12px">
        Running now adds an extra occurrence and leaves the schedule alone — the next fire time is
        unchanged. Last outcome is the most recently <em>created</em> run, so an occurrence still
        going shows as running rather than reporting the previous night's result.
      </p>
    }
  `,
})
export class Recurring {

  protected readonly page = new CursorStack();
  protected readonly time = formatTime;
  protected readonly busy = signal(false);
  protected readonly triggered = signal<string | null>(null);
  protected readonly actionError = signal<string | null>(null);

  private readonly loaded = asyncSignal(() =>
    api.recurring({ cursor: this.page.cursor(), limit: 25 }),
  );

  protected readonly result = this.loaded.state;
  protected readonly isOverdue = isOverdue;
  protected readonly dueText = dueText;

  protected async trigger(id: string): Promise<void> {
    this.busy.set(true);
    this.triggered.set(null);
    this.actionError.set(null);
    try {
      await api.triggerRecurring(id);
      this.triggered.set(id);
      // The definition's own row does not change — NextFireTime is deliberately untouched — but the
      // job it enqueued is now real, so anything derived from the list should be re-read.
      this.loaded.reload();
    } catch (error: unknown) {
      this.actionError.set(errorMessage(error));
    } finally {
      this.busy.set(false);
    }
  }
}
