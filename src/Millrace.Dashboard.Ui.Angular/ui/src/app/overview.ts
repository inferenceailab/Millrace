import { ChangeDetectionStrategy, Component, computed } from '@angular/core';
import { api } from '../../../../ui-shared/api';
import { jobStates } from '../../../../ui-shared/contract';
import { asyncSignal, Chip, ErrorNotice, Loading } from './shared';

/** Every figure answers one question, so these are tiles, not a chart. */
@Component({
  selector: 'mr-overview',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Chip, ErrorNotice, Loading],
  template: `
    @if (result().loading) {
      <mr-loading />
    } @else if (result().error; as error) {
      <mr-error [message]="error" />
    } @else if (result().data; as data) {
      <section>
        <h2>Jobs by state</h2>
        <div class="tiles">
          <!--
            Indexed without a fallback: the conformance kit requires every state to be present in
            jobsByState, so a missing key is a provider bug rather than something to paper over.
          -->
          @for (state of states; track state) {
            <div class="tile" [class.is-zero]="data.jobsByState[state] === 0">
              <div class="label"><mr-chip [state]="state" /></div>
              <div class="value">{{ data.jobsByState[state].toLocaleString() }}</div>
            </div>
          }
        </div>
      </section>

      <section>
        <h2>Schedules</h2>
        <div class="tiles">
          <div class="tile">
            <div class="label">Recurring</div>
            <div class="value">{{ data.recurringDefinitions.toLocaleString() }}</div>
          </div>
          <div class="tile" [class.is-zero]="data.overdueRecurringDefinitions === 0">
            <div class="label">Overdue</div>
            <div class="value" [class.overdue]="data.overdueRecurringDefinitions > 0">
              {{ data.overdueRecurringDefinitions.toLocaleString() }}
            </div>
          </div>
        </div>
        @if (data.overdueRecurringDefinitions > 0) {
          <div class="notice error">
            <strong>{{ data.overdueRecurringDefinitions }} schedule(s) are overdue.</strong>
            Either the scheduler is behind, or no node is running the scheduler role.
          </div>
        }
      </section>

      <section>
        <h2>Queue depth</h2>
        @if (queues().length === 0) {
          <p class="muted">Nothing claimable.</p>
        } @else {
          <div class="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>Queue</th>
                  <th class="num">Enqueued</th>
                </tr>
              </thead>
              <tbody>
                @for (queue of queues(); track queue[0]) {
                  <tr>
                    <td>{{ queue[0] }}</td>
                    <td class="num">{{ queue[1].toLocaleString() }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </section>
    }
  `,
})
export class Overview {

  protected readonly states = jobStates;
  protected readonly result = asyncSignal(() => api.statistics()).state;

  protected readonly queues = computed(() =>
    Object.entries(this.result().data?.enqueuedByQueue ?? {}).sort((a, b) => b[1] - a[1]),
  );
}
