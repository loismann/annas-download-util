import { HttpInterceptorFn } from '@angular/common/http';
import { mockDataInterceptor } from '../app/interceptors/mock-data.interceptor';

/**
 * Development environment.
 *
 * `angular.json` swaps this file for `environment.prod.ts` in the production
 * configuration (`fileReplacements`). That swap is the whole point: because the
 * production copy never imports `mock-data.interceptor`, ~345 lines of fixture
 * prose are dropped from the bundle at build time rather than merely being
 * skipped at runtime. A plain `if (production)` guard could not do that — the
 * static import alone is enough to pull the fixtures in.
 */
export const environment = {
  production: false,

  /** Interceptors that exist only in development. The mock interceptor serves
   *  canned responses so the frontend runs with no backend; it is additionally
   *  gated on hostname === 'localhost' at runtime. */
  devInterceptors: [mockDataInterceptor] as HttpInterceptorFn[]
};
