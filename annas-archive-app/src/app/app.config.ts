import { ApplicationConfig, ErrorHandler, provideZoneChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withInterceptors, withFetch } from '@angular/common/http';
import { provideAnimations } from '@angular/platform-browser/animations';

import { routes } from './app.routes';
import { authInterceptor } from './interceptors/auth.interceptor';
import { errorInterceptor } from './interceptors/error.interceptor';
import { dateNightImpersonationInterceptor } from './interceptors/date-night-impersonation.interceptor';
import { GlobalErrorHandler } from './services/global-error-handler.service';
import { environment } from '../environments/environment';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    // Path-based URLs (/library), not hash (/#/library). Requires the server to
    // serve index.html for unknown paths — Program.cs does via MapFallbackToFile,
    // and index.html already declares <base href="/">. Hash routing also hid
    // pre-fragment query strings from the router, which silently broke the
    // Spotify OAuth callback's ?spotify= result.
    provideRouter(routes),
    provideHttpClient(
      // devInterceptors is empty in production and the module it came from is
      // never imported there, so the mock fixtures are absent from the bundle
      // rather than merely inert. It stays first so it can short-circuit a
      // request before the real interceptors touch it.
      withInterceptors([
        ...environment.devInterceptors,
        errorInterceptor,
        authInterceptor,
        dateNightImpersonationInterceptor
      ]),
      withFetch()
    ),
    provideAnimations(),
    { provide: ErrorHandler, useClass: GlobalErrorHandler }
  ]
};
