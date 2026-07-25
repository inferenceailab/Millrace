import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { api } from '../../../../ui-shared/api';
import { asyncSignal, Chip, CursorStack, ErrorNotice, formatTime, Loading, Pager } from './shared';

@Component({
  selector: 'mr-instances',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Chip, ErrorNotice, Loading, Pager],
  template: `
    <div class="controls">
      <div class="field">
        <label for="definition">Definition</label>
        <input
          id="definition"
          type="text"
          placeholder="Any"
          [value]="definitionId()"
          (input)="setDefinition($event)"
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
              <th>Definition</th>
              <th class="num">Version</th>
              <th>Created</th>
              <th>Last checkpoint</th>
              <th class="num">Revision</th>
            </tr>
          </thead>
          <tbody>
            @for (instance of data.items; track instance.id) {
              <tr>
                <td><mr-chip [state]="instance.state" /></td>
                <td>
                  {{ instance.definitionId }}
                  <div class="muted mono">{{ instance.id }}</div>
                </td>
                <td class="num">{{ instance.definitionVersion }}</td>
                <td>{{ time(instance.createdAt) }}</td>
                <td>{{ time(instance.updatedAt) }}</td>
                <td class="num">{{ instance.revision }}</td>
              </tr>
            } @empty {
              <tr>
                <td colspan="6" class="muted">No workflow instances.</td>
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
export class Instances {

  protected readonly definitionId = signal('');
  protected readonly page = new CursorStack();
  protected readonly time = formatTime;

  protected readonly result = asyncSignal(() =>
    api.instances({
      definitionId: this.definitionId() || undefined,
      cursor: this.page.cursor(),
      limit: 25,
    }),
  ).state;

  protected setDefinition(event: Event): void {
    this.definitionId.set((event.target as HTMLInputElement).value);
    this.page.reset();
  }
}
