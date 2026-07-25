using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Millrace.Spike.BlazorHost.Components;

/// <summary>
/// GLUE 2 of 2 — what a Blazor host must write to receive one <c>CustomEvent</c> from the element.
/// </summary>
/// <remarks>
/// <para>
/// React and Angular need none of this. Angular binds <c>(job-select)="onSelect($event)"</c> and
/// reads <c>$event.detail</c>; React attaches a listener through a ref. Blazor has no equivalent
/// escape hatch: a DOM event it does not already know needs a declared args type, an
/// <c>[EventHandler]</c> registration, <em>and</em> a JavaScript factory that maps the event to
/// those args (<c>wwwroot/millrace-interop.js</c>).
/// </para>
/// <para>
/// It works — that is the finding, not a failure. But it is per-event, it is JavaScript, and it has
/// to be written by the consumer of a package whose selling point was not writing JavaScript.
/// </para>
/// </remarks>
public sealed class JobSelectEventArgs : EventArgs
{
    public string Id { get; set; } = string.Empty;
}

[EventHandler("onjobselect", typeof(JobSelectEventArgs), enableStopPropagation: true,
    enablePreventDefault: true)]
public static class EventHandlers;
