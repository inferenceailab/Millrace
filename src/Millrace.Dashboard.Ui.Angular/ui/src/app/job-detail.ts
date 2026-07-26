import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { api } from '../../../../ui-shared/api';
import { isCancellable, isRequeueable, isRunnableNow } from '../../../../ui-shared/contract';
import { asyncSignal, Chip, ErrorNotice, errorMessage, formatTime, Loading } from './shared';

@Component({
  selector: 'mr-job-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Chip, ErrorNotice, Loading],
  template: `
    <p><a href="#/jobs">← All jobs</a></p>

    @if (result().loading) {
      <mr-loading />
    } @else if (result().error; as error) {
      <mr-error [message]="error" />
    } @else if (result().data; as data) {
      <h2>{{ data.summary.methodName }} <mr-chip [state]="data.summary.state" /></h2>

      <div class="controls">
        <button type="button" (click)="cancel()" [disabled]="busy() || !cancellable()">
          Cancel
        </button>
        <button type="button" (click)="runNow()" [disabled]="busy() || !runnableNow()">
          Run now
        </button>
        <button type="button" (click)="requeue()" [disabled]="busy() || !requeueable()">
          Requeue
        </button>
        @if (action(); as message) {
          <span class="muted">{{ message }}</span>
        }
        @if (actionError(); as message) {
          <span class="overdue">{{ message }}</span>
        }
      </div>
      <p class="muted">
        @if (cancellable()) {
          Cancelling a running job is cooperative — it is asked to stop, and one about to finish may
          still succeed.
        } @else if (requeueable()) {
          Requeue runs this work again as a <em>new</em> job. This record has finished and stays as
          it is; the new job links back to it.
        }
      </p>

      <dl class="detail-grid">
        <dt>Id</dt>
        <dd class="mono">{{ data.summary.id }}</dd>
        <dt>Type</dt>
        <dd class="mono">{{ data.summary.typeName }}</dd>
        <dt>Queue</dt>
        <dd>{{ data.summary.queue }} · priority {{ data.summary.priority }}</dd>
        <dt>Created</dt>
        <dd>{{ time(data.summary.createdAt) }}</dd>
        <dt>Due</dt>
        <dd>{{ time(data.summary.dueAt) }}</dd>
        <dt>Finished</dt>
        <dd>{{ time(data.summary.finishedAt) }}</dd>
        <dt>Attempts</dt>
        <dd>
          {{ data.summary.attempt }} started · {{ data.summary.failures }} failed ·
          {{ data.summary.interruptions }} interrupted
          <div class="muted">
            Interruptions are executions killed by infrastructure — crashes, deploys, lost leases —
            rather than by failing code. Per-attempt history is not stored, so there is no timeline.
          </div>
        </dd>
        <dt>Worker</dt>
        <dd>{{ data.summary.workerId ?? '—' }}</dd>
        <dt>Lease until</dt>
        <dd>{{ time(data.leaseUntil) }}</dd>
        <dt>Tenant</dt>
        <dd>{{ data.summary.tenantId ?? '—' }}</dd>
        <dt>Idempotency key</dt>
        <dd class="mono">{{ data.idempotencyKey ?? '—' }}</dd>
        <dt>Cancel requested</dt>
        <dd>{{ data.cancelRequested ? 'yes' : 'no' }}</dd>
        @if (data.parentId; as parentId) {
          <dt>Continuation of</dt>
          <dd><a class="mono" [href]="'#/jobs/' + parentId">{{ parentId }}</a></dd>
        }
      </dl>

      <h2>Arguments</h2>
      <pre>{{ argumentsText() }}</pre>

      @if (data.attempts.length > 0) {
        <h2>Attempts</h2>
        <div class="table-scroll">
          <table>
            <thead>
              <tr>
                <th class="num">#</th>
                <th>Outcome</th>
                <th>Ended</th>
                <th>Worker</th>
                <th>Error</th>
              </tr>
            </thead>
            <tbody>
              @for (a of data.attempts; track a.attempt) {
                <tr>
                  <td class="num">{{ a.attempt }}</td>
                  <td>
                    <mr-chip [state]="a.outcome === 'Failed' ? 'Failed' : 'Cancelled'" />
                    @if (a.outcome === 'Interrupted') {
                      <span> interrupted</span>
                    }
                  </td>
                  <td>{{ time(a.recordedAt) }}</td>
                  <td class="mono">{{ a.workerId ?? '—' }}</td>
                  <td>
                    @if (a.error; as error) {
                      <pre>{{ error }}</pre>
                    } @else {
                      <span class="muted">no verdict recorded</span>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        <p class="muted" style="margin-top: 12px">
          Only failed and interrupted executions are listed — a successful attempt records nothing,
          so a job that worked first time shows none. Bounded per job, while the counts above are
          exact.
        </p>
      }

      @if (data.lastError; as lastError) {
        <h2>Last error</h2>
        <pre>{{ lastError }}</pre>
      }
    }
  `,
})
export class JobDetail {

  readonly id = input.required<string>();

  protected readonly time = formatTime;
  protected readonly busy = signal(false);
  protected readonly action = signal<string | null>(null);
  protected readonly actionError = signal<string | null>(null);

  private readonly loaded = asyncSignal(() => api.job(this.id()));
  protected readonly result = this.loaded.state;

  // Which actions apply to which state is a fact about the contract, not about Angular, so both
  // predicates live beside the types they read.
  protected readonly cancellable = computed(() => {
    const state = this.result().data?.summary.state;
    return state !== undefined && isCancellable(state);
  });

  protected readonly runnableNow = computed(() => {
    const state = this.result().data?.summary.state;
    return state !== undefined && isRunnableNow(state);
  });

  protected readonly requeueable = computed(() => {
    const state = this.result().data?.summary.state;
    return state !== undefined && isRequeueable(state);
  });

  protected readonly argumentsText = computed(() => {
    const invocation = this.result().data?.invocation;
    if (!invocation || invocation.parameterTypes.length === 0) return '(none)';
    return invocation.parameterTypes
      .map((type, i) => `${type}\n  ${invocation.argumentsJson[i] ?? 'null'}`)
      .join('\n\n');
  });

  protected async cancel(): Promise<void> {
    await this.run(async () => {
      await api.cancelJob(this.id());
      // Deliberately not "cancelled": a running job is asked to stop, and the answer does not
      // promise the work did not happen.
      return 'Cancellation requested.';
    });
  }

  protected async runNow(): Promise<void> {
    await this.run(async () => {
      await api.runJobNow(this.id());
      return 'Running now.';
    });
  }

  protected async requeue(): Promise<void> {
    await this.run(async () => {
      const requeued = await api.requeueJob(this.id());
      window.location.hash = `#/jobs/${requeued.id}`;
      return 'Requeued.';
    });
  }

  private async run(action: () => Promise<string>): Promise<void> {
    this.busy.set(true);
    this.action.set(null);
    this.actionError.set(null);
    try {
      this.action.set(await action());
      this.loaded.reload();
    } catch (error: unknown) {
      this.actionError.set(errorMessage(error));
    } finally {
      this.busy.set(false);
    }
  }
}
