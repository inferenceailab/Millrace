// Hash routing, done by hand.
//
// Blazor intercepts same-origin anchor clicks and navigates with history.pushState. For a link that
// only changes the fragment that means the URL updates and *nothing* is raised: a tab click was
// measured as pushState 1, hashchange 0, and NavigationManager.LocationChanged stayed silent too.
// So the app does not click links — it calls push() below, and listens for the two events the
// browser raises when navigation happens without it.
//
//   popstate    — back and forward
//   hashchange  — the fragment edited directly in the address bar

let handler = null;

export function subscribe(owner) {
    handler = () => owner.invokeMethodAsync('HashChanged', window.location.hash);
    window.addEventListener('popstate', handler);
    window.addEventListener('hashchange', handler);
}

export function push(hash) {
    // pushState rather than assigning location.hash: assigning raises hashchange, which would route
    // twice for one navigation. The caller has already updated its own state.
    window.history.pushState(null, '', hash);
}

export function unsubscribe() {
    if (handler !== null) {
        window.removeEventListener('popstate', handler);
        window.removeEventListener('hashchange', handler);
        handler = null;
    }
}
