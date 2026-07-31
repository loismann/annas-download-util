import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { AuthService } from '../services/auth.service';
import { DateNightImpersonationService } from '../services/date-night-impersonation.service';

/** Audience-facing Date Night routes whose backend handler honors the
 * X-Date-Night-As override (see DateNightEndpoints.ResolveDateNightPerson).
 * Deliberately excludes /pool, /announcement, /availability, and anything
 * under /admin/ — those either ignore the header or already take an explicit
 * Person in the request body. */
const IMPERSONATABLE_PATHS = [
  '/api/date-night/cycle',
  '/api/date-night/skip',
  '/api/date-night/showtime-check'
];

export const dateNightImpersonationInterceptor: HttpInterceptorFn = (req, next) => {
  const authService = inject(AuthService);
  const impersonation = inject(DateNightImpersonationService);

  const person = impersonation.current();
  const isImpersonatable = IMPERSONATABLE_PATHS.some(p => req.url.includes(p)) && !req.url.includes('/admin/');

  if (person && authService.isAdmin() && isImpersonatable) {
    return next(req.clone({ setHeaders: { 'X-Date-Night-As': person } }));
  }

  return next(req);
};
