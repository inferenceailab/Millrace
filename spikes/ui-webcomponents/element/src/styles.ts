import { css } from 'lit'

/**
 * The stylesheet re-cut for a shadow root.
 *
 * THIS FILE IS THE MEASUREMENT. `src/ui-shared/millrace.css` is 28 custom properties and 37 class
 * selectors. The properties inherit through a shadow boundary and are therefore *not* here — the
 * host page keeps supplying `--surface`, `--ink`, the status roles, and a consumer's theme toggle
 * still reaches inside. That part of the "tokens already work" claim holds.
 *
 * The class selectors do not inherit, so every one this view touches had to be copied in. That is
 * 24 of the 37, reproduced below, and it is the real cost of the shadow boundary: not a port, a
 * second copy of the visual layer that must be kept in step with the original by hand.
 *
 * Copied (24): .controls .field, label, select, input, button, button:disabled, .table-scroll,
 * table, th, td, th/td.num, .mono, .muted, .chip, .chip::before, and the six .chip[data-state]
 * variants, .pager, .notice, .notice.error.
 *
 * Not copied (13, because this view does not use them): .layout, .masthead and its two children,
 * nav.tabs and its two states, .tiles, .tile and its three children, .detail-grid and its two
 * children, pre, .overdue. A second element covering the job *detail* view would need most of them,
 * and would face the same choice again.
 */
export const jobsStyles = css`
  :host {
    display: block;
    /*
      Fallbacks matter here in a way they do not in the shipping UIs: an element dropped into a page
      that never loaded millrace.css must still be legible, because the host chose the element, not
      the stylesheet.
    */
    color: var(--ink, #0b0b0b);
    font-family: var(--font, system-ui, -apple-system, 'Segoe UI', sans-serif);
    font-size: 14px;
    line-height: 1.5;
  }

  .controls {
    display: flex;
    gap: 10px;
    align-items: flex-end;
    flex-wrap: wrap;
    margin-bottom: 14px;
  }

  .field {
    display: flex;
    flex-direction: column;
    gap: 4px;
  }

  .field label {
    font-size: 12px;
    color: var(--ink-secondary, #52514e);
    font-weight: 500;
  }

  select,
  input[type='text'],
  button {
    font: inherit;
    color: inherit;
    background: var(--surface, #fcfcfb);
    border: 1px solid var(--border, rgba(11, 11, 11, 0.1));
    border-radius: 6px;
    padding: 6px 10px;
  }

  button {
    cursor: pointer;
    font-weight: 500;
  }

  button:disabled {
    color: var(--ink-muted, #898781);
    cursor: not-allowed;
  }

  .table-scroll {
    overflow-x: auto;
  }

  table {
    width: 100%;
    border-collapse: collapse;
    background: var(--surface, #fcfcfb);
    border: 1px solid var(--border, rgba(11, 11, 11, 0.1));
    border-radius: var(--radius, 8px);
    overflow: hidden;
  }

  th,
  td {
    text-align: left;
    padding: 9px 12px;
    border-bottom: 1px solid var(--hairline, #e1e0d9);
    vertical-align: top;
  }

  th {
    color: var(--ink-secondary, #52514e);
    font-size: 12px;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.04em;
  }

  tbody tr:last-child td {
    border-bottom: none;
  }

  td.num,
  th.num {
    text-align: right;
    font-variant-numeric: tabular-nums;
  }

  .mono {
    font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
    font-size: 12px;
  }

  .muted {
    color: var(--ink-muted, #898781);
  }

  /* State chips: a dot carries the status colour, the text carries the meaning. */
  .chip {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    white-space: nowrap;
    font-weight: 500;
  }

  .chip::before {
    content: '';
    width: 8px;
    height: 8px;
    border-radius: 50%;
    background: var(--ink-muted, #898781);
    flex: none;
  }

  .chip[data-state='Succeeded']::before {
    background: var(--good, #0ca30c);
  }
  .chip[data-state='Failed']::before {
    background: var(--warning, #fab219);
  }
  .chip[data-state='Processing']::before {
    background: var(--accent, #2a78d6);
  }
  .chip[data-state='Dead']::before {
    background: var(--critical, #d03b3b);
  }

  .pager {
    display: flex;
    gap: 8px;
    align-items: center;
    margin-top: 12px;
    color: var(--ink-muted, #898781);
    font-size: 12px;
  }

  .notice {
    padding: 14px 16px;
    border: 1px solid var(--border, rgba(11, 11, 11, 0.1));
    border-left: 3px solid var(--accent, #2a78d6);
    border-radius: var(--radius, 8px);
    background: var(--surface, #fcfcfb);
    color: var(--ink-secondary, #52514e);
  }

  .notice.error {
    border-left-color: var(--critical, #d03b3b);
  }
`
