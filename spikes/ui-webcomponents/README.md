# Spike: one rendered implementation, or one non-visual core? — [#86](https://github.com/inferenceailab/Millrace/issues/86)

**Answer: keep per-framework rendering. Build the Blazor UI natively.** Web components work — that
is not the problem — but for this codebase they cost the most where they were supposed to help most.

Run it:

```
cd spikes/ui-webcomponents/element && npm install && npm run build
dotnet run --project spikes/ui-webcomponents/hosts/Millrace.Spike.BlazorHost
# http://127.0.0.1:5219/        Blazor host
# http://127.0.0.1:5219/plain.html   no framework at all
```

## What was built

`<millrace-jobs>` — the Jobs view (filters, cursor paging, row selection) as one Lit element, using
the same `src/ui-shared/` client the shipping UIs use. 32 kB, **10.5 kB gzipped**. Hosted in Blazor
and in plain HTML, both verified in a browser; the React and Angular integrations are written as
`hosts/react` and `hosts/angular` but were **not executed** — their glue is the measurement, and
the DOM mechanisms they rely on are the ones the plain-HTML host proves.

## What the browser confirmed

| | Result |
|---|---|
| Design tokens cross the shadow boundary | **yes** — `--surface: #fcfcfb` on the page reached the table inside the shadow root as `rgb(252,252,251)` |
| `CustomEvent` crosses it | **yes**, with `composed: true` |
| A property holding a *render function* | **yes** from JS |
| Blazor receives the event | **yes** — clicking a row set `Selected job: 019f9af2-…` in C# |
| Blazor sets a filter | **yes** — `job-state="Failed"` from Razor re-queried the element |

So the mechanism is sound in every host. The findings are about cost.

## Finding 1 — the stylesheet is a second copy of the visual layer, per view

`src/ui-shared/millrace.css` is **28 custom properties and 37 class selectors**. The properties
inherit through the boundary (confirmed above), so the "design tokens already work" argument holds.

The class selectors do not, and **24 of the 37 had to be copied into `element/src/styles.ts`** — for
the *simplest* view in the dashboard. The 13 left out are the ones this view happens not to use;
a job-detail element would need most of them, and would face the same choice again.

That is the real cost, and it is not one-off: it is a duplicate of the visual layer that must be
kept in step with the original by hand, forever, with nothing to catch drift. Light DOM avoids it
entirely — but then the element is not encapsulated and the whole argument for it weakens.

## Finding 2 — Blazor pays the most and gets the least

This is the deciding one, because Blazor is the only reason to consider this shape at all.

**Receiving one event needs two files of glue** (`wwwroot/millrace-interop.js` and
`Components/JobSelectEventArgs.cs`): a JS `registerCustomEventType` call, an `EventArgs` class, and
an `[EventHandler]` registration. Per event. Angular needs **none** — `(job-select)="onSelect($event)"`
already works. React needs a ref, a listener and a cleanup: **3 lines**.

**And it fails silently when wrong.** The first run threw `Blazor is not defined` because the interop
script loaded before `blazor.web.js`. The *quieter* failure is worse: register after `Blazor.start()`
and the handler simply never fires, with no error at all. A consumer debugs that as "the element is
broken".

**The function-based extension point is closed to Blazor entirely.** `extraColumns` takes objects
with a `render` function. Razor cannot supply a JavaScript function, so the Blazor host cannot add a
column the way React and Angular can — it must write JavaScript or fork the element. The spike page
says so on screen rather than hiding it.

Net: a Blazor consumer writes JavaScript to use a package whose selling point is that they don't.

## Finding 3 — the API base has to become explicit

Not anticipated. The shipping UIs derive the API base from their own URL, because they are always
served from `{prefix}/ui`. **An element embedded in someone else's page has no such guarantee** — its
URL is the host's — so the first run 404'd every request against `/api/v1/jobs`.

Fixed by adding `setApiBase()` to `src/ui-shared/api.ts` and a `base` attribute on the element. One
attribute is the whole cost, but it is not optional, and it generalises: any UI that stops owning
the page needs its configuration passed in rather than inferred.

## Finding 4 — the savings are smaller than they look

The estimate that prompted this was "60–70% of a UI is non-visual". That was right — and it is
**already shared** as of §11.21: contract types, API client, formatting and state predicates live in
`src/ui-shared/`. What web components would additionally share is the *rendering*, which is the part
where each framework's idiom is actually wanted, and the part that costs a duplicated stylesheet to
encapsulate.

## What would change the answer

- **Dropping Blazor as a first-class target.** Almost all the cost is there. For React + Angular
  alone, a Lit element is a reasonable trade.
- **Designing shadow-first from the start.** The 24-selector copy is a migration cost, not an
  inherent one. A stylesheet authored as per-component styles plus tokens would not pay it.
- **A much larger UI.** One view justifies little either way. This is worth revisiting if the
  dashboard grows well past its current five views.

## Also found

The spike surfaced a shipping bug unrelated to its question:
[#89](https://github.com/inferenceailab/Millrace/issues/89) — enum *values* serialize as integers
while every UI declares them as strings, so state chips render `0`..`7` and the management buttons
merged in #88 enable backwards. The State column showing `0` is what gave it away.
