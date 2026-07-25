import { ChangeDetectionStrategy, Component, computed, signal } from '@angular/core';
import { api, ApiError } from '../../../../ui-shared/api';
import { errorMessage } from '../../../../ui-shared/format';

/**
 * Sending a signal by hand.
 *
 * The payload is raw JSON rather than a form, because the workflow definition declares the payload
 * type and the engine binds on its side of the wire (§11.5) — the dashboard has no schema to render
 * a form from, and pretending otherwise would be a lie about what it knows.
 */
@Component({
  selector: 'mr-signals',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p class="muted">
      Delivers a signal to an instance waiting on this name and correlation id. Delivery is
      at-most-once, so "nothing was waiting" is a normal answer rather than a fault — a signal sent
      before the instance reaches its wait is simply lost.
    </p>

    <div class="controls">
      <div class="field">
        <label for="signal-name">Name</label>
        <input
          id="signal-name"
          type="text"
          placeholder="OrderApproved"
          [value]="name()"
          (input)="name.set(value($event))"
        />
      </div>
      <div class="field">
        <label for="signal-correlation">Correlation id</label>
        <input
          id="signal-correlation"
          type="text"
          placeholder="order-42"
          [value]="correlationId()"
          (input)="correlationId.set(value($event))"
        />
      </div>
      <button type="button" (click)="send()" [disabled]="busy() || !ready()">Send</button>
    </div>

    <div class="field">
      <label for="signal-payload">Payload (JSON, optional)</label>
      <textarea
        id="signal-payload"
        rows="6"
        spellcheck="false"
        style="font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 12px"
        [value]="payload()"
        (input)="payload.set(value($event))"
      ></textarea>
    </div>

    @if (payloadError(); as error) {
      <div class="notice error"><strong>Payload is not valid JSON.</strong> {{ error }}</div>
    }
    @if (result(); as message) {
      <div class="notice">{{ message }}</div>
    }
    @if (failure(); as message) {
      <div class="notice error"><strong>Not delivered.</strong> {{ message }}</div>
    }
  `,
})
export class Signals {

  protected readonly name = signal('');
  protected readonly correlationId = signal('');
  protected readonly payload = signal('');
  protected readonly busy = signal(false);
  protected readonly result = signal<string | null>(null);
  protected readonly failure = signal<string | null>(null);

  /** Validated here so a typo fails before it reaches a workflow, not inside one. */
  protected readonly payloadError = computed(() => {
    const text = this.payload().trim();
    if (text.length === 0) return null;
    try {
      JSON.parse(text);
      return null;
    } catch (error: unknown) {
      return error instanceof Error ? error.message : String(error);
    }
  });

  protected readonly ready = computed(
    () =>
      this.name().trim().length > 0 &&
      this.correlationId().trim().length > 0 &&
      this.payloadError() === null,
  );

  protected value(event: Event): string {
    return (event.target as HTMLInputElement | HTMLTextAreaElement).value;
  }

  protected async send(): Promise<void> {
    this.busy.set(true);
    this.result.set(null);
    this.failure.set(null);
    try {
      await api.sendSignal(this.name().trim(), this.correlationId().trim(), this.payload().trim());
      this.result.set('Delivered.');
    } catch (error: unknown) {
      this.failure.set(
        error instanceof ApiError && error.status === 404
          ? 'No instance is waiting on that name and correlation id.'
          : errorMessage(error),
      );
    } finally {
      this.busy.set(false);
    }
  }
}
