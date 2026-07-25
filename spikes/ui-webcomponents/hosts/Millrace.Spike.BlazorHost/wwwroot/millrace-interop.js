// GLUE 1 of 2 — teaching Blazor about one custom event.
//
// Without this, `@onjobselect` compiles, renders, and never fires. There is no warning: Blazor
// simply has no mapping from the DOM event to the args type, so the handler is dead. That silence
// is the part worth recording — a consumer would debug it as "the element is broken".
//
// It is also per-event. A second element with a second event needs a second block here, a second
// args class, and a second [EventHandler] registration.
Blazor.registerCustomEventType('jobselect', {
  browserEventName: 'job-select',
  createEventArgs: (event) => ({ id: event.detail.id }),
});
