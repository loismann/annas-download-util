// src/app/app.config.server.ts
import { ApplicationConfig } from '@angular/core';
import {
  provideHttpClient,
  withInterceptorsFromDi
} from '@angular/common/http';

// whatever else you already export (router, hydration, etc.)
export const config: ApplicationConfig = {
  providers: [
    /* existing providers … */

    // 👇 THIS wires up HttpClient for the SSR injector
    provideHttpClient(withInterceptorsFromDi())
  ]
};
