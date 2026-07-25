import { ChangeDetectionStrategy, Component, computed } from '@angular/core';
import { api } from '../../../../ui-shared/api';
import { asyncSignal, routeSignal } from './shared';
import { Instances } from './instances';
import { JobDetail } from './job-detail';
import { Jobs } from './jobs';
import { Overview } from './overview';
import { Recurring } from './recurring';
import { Signals } from './signals';

const tabs = [
  { href: '#/', label: 'Overview', prefix: '/' },
  { href: '#/jobs', label: 'Jobs', prefix: '/jobs' },
  { href: '#/recurring', label: 'Recurring', prefix: '/recurring' },
  { href: '#/instances', label: 'Workflows', prefix: '/instances' },
  { href: '#/signals', label: 'Signals', prefix: '/signals' },
];

@Component({
  selector: 'millrace-app',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Instances, JobDetail, Jobs, Overview, Recurring, Signals],
  template: `
    <div class="layout">
      <header class="masthead">
        <h1>Millrace</h1>
        <span class="sub">Durable jobs and workflow orchestration</span>
        @if (info().data; as info) {
          <span class="sub">{{ info.version }} · {{ info.storageProvider }}</span>
        }
      </header>

      <nav class="tabs">
        @for (tab of tabs; track tab.href) {
          <a [href]="tab.href" [attr.aria-current]="current() === tab.prefix ? 'page' : null">
            {{ tab.label }}
          </a>
        }
      </nav>

      <main>
        @if (jobId(); as id) {
          <mr-job-detail [id]="id" />
        } @else {
          @switch (current()) {
            @case ('/jobs') {
              <mr-jobs />
            }
            @case ('/recurring') {
              <mr-recurring />
            }
            @case ('/instances') {
              <mr-instances />
            }
            @case ('/signals') {
              <mr-signals />
            }
            @default {
              <mr-overview />
            }
          }
        }
      </main>
    </div>
  `,
})
export class App {
  private readonly route = routeSignal();

  protected readonly tabs = tabs;

  // The backend's own report, so the header names the version and provider actually running rather
  // than the one this bundle was built against.
  protected readonly info = asyncSignal(() => api.info()).state;

  protected readonly jobId = computed(() => /^\/jobs\/([^/]+)$/.exec(this.route())?.[1] ?? null);

  protected readonly current = computed(() => {
    const route = this.route();
    return tabs.find((tab) => tab.prefix !== '/' && route.startsWith(tab.prefix))?.prefix ?? '/';
  });
}
