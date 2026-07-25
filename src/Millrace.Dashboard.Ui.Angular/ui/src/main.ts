import { bootstrapApplication } from '@angular/platform-browser';
import { provideZonelessChangeDetection } from '@angular/core';
import { App } from './app/app';

// Zoneless: the whole application is signals, so zone.js would be a polyfill nobody reads and
// ~30 kB shipped to every consumer for nothing.
void bootstrapApplication(App, {
  providers: [provideZonelessChangeDetection()],
});
