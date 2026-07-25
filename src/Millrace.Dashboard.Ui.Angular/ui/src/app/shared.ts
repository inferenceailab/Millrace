import {
  Component,
  ChangeDetectionStrategy,
  computed,
  effect,
  input,
  output,
  signal,
  type Signal,
  type WritableSignal,
} from '@angular/core';

/** Hash routing: no router dependency, and it survives any mount prefix without configuration. */
export function routeSignal(): Signal<string> {
  const route = signal(window.location.hash.slice(1) || '/');
  window.addEventListener('hashchange', () => route.set(window.location.hash.slice(1) || '/'));
  return route.asReadonly();
}

export interface Async<T> {
  data?: T;
  error?: string;
  loading: boolean;
}

/**
 * Reloads whenever a signal read inside `load` changes, discarding results from superseded requests.
 *
 * Must be called from an injection context (a component field initializer), because the tracking is
 * an `effect`. A generation counter rather than an abort: a slow first request must not overwrite a
 * fast second one, which is the bug that makes a filtered table flicker back to the old rows.
 */
export function asyncSignal<T>(load: () => Promise<T>): {
  state: Signal<Async<T>>;
  reload: () => void;
} {
  const state: WritableSignal<Async<T>> = signal({ loading: true });
  const nonce = signal(0);
  let generation = 0;

  effect(() => {
    nonce();
    const mine = ++generation;
    state.set({ loading: true });
    load().then(
      (data) => generation === mine && state.set({ data, loading: false }),
      (error: unknown) =>
        generation === mine &&
        state.set({
          loading: false,
          error: error instanceof Error ? error.message : String(error),
        }),
    );
  });

  return { state: state.asReadonly(), reload: () => nonce.update((n) => n + 1) };
}

// Formatting is framework-free and shared with the React UI, so the two dashboards never render the
// same instant differently.
export {
  dueText,
  errorMessage,
  formatTime,
  isOverdue,
  relativeToNow,
} from '../../../../ui-shared/format';

/** The dot carries colour, the text carries meaning — never colour alone. */
@Component({
  selector: 'mr-chip',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="chip" [attr.data-state]="state()">{{ state() }}</span>`,
})
export class Chip {
  readonly state = input.required<string>();
}

@Component({
  selector: 'mr-loading',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<p class="muted">Loading…</p>`,
})
export class Loading {}

@Component({
  selector: 'mr-error',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<div class="notice error"><strong>Request failed.</strong> {{ message() }}</div>`,
})
export class ErrorNotice {
  readonly message = input.required<string>();
}

/**
 * Next/previous paging over opaque cursors.
 *
 * There is no page number and no total, because §11.12 does not ship one. "Previous" works by
 * keeping the cursors already visited on a stack rather than by arithmetic.
 */
export class CursorStack {
  private readonly stack = signal<(string | null)[]>([null]);

  readonly cursor = computed(() => this.stack().at(-1) ?? null);
  readonly canGoBack = computed(() => this.stack().length > 1);

  next(cursor: string): void {
    this.stack.update((s) => [...s, cursor]);
  }

  back(): void {
    this.stack.update((s) => (s.length > 1 ? s.slice(0, -1) : s));
  }

  /** Called when a filter changes: the cursors already visited describe the old query. */
  reset(): void {
    this.stack.set([null]);
  }
}

@Component({
  selector: 'mr-pager',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="pager">
      <button type="button" (click)="back.emit()" [disabled]="!canGoBack()">← Previous</button>
      <button type="button" (click)="next.emit()" [disabled]="!nextCursor()">Next →</button>
      <span>{{ count() }} row{{ count() === 1 ? '' : 's' }} on this page</span>
    </div>
  `,
})
export class Pager {
  readonly nextCursor = input.required<string | null>();
  readonly canGoBack = input.required<boolean>();
  readonly count = input.required<number>();
  readonly next = output<void>();
  readonly back = output<void>();
}
