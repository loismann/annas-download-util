import { HttpInterceptorFn } from '@angular/common/http';

/**
 * Production environment — substituted for `environment.ts` by the
 * `fileReplacements` entry in angular.json's production configuration.
 *
 * Deliberately imports nothing from `app/interceptors/mock-data.interceptor`:
 * that absence is what keeps the fixtures out of the shipped bundle.
 */
export const environment = {
  production: true,

  /** No development-only interceptors in production. */
  devInterceptors: [] as HttpInterceptorFn[]
};
