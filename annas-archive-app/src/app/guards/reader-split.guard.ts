import { CanActivateFn, Router } from '@angular/router';
import { inject } from '@angular/core';
import { AuthService } from '../services/auth.service';

/**
 * While both readers exist, each person has exactly one: the admin lives on
 * Reader II, and everyone else stays on Reader I until it is retired.
 *
 * A redirect rather than a refusal, in both directions — the person asked to
 * read, and the app knows which reader is theirs, so it takes them there
 * instead of arguing. Retiring Reader I deletes this file along with it.
 */
export const reader2Guard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (!auth.isAuthenticated()) {
    router.navigate(['/login']);
    return false;
  }

  if (auth.isAdmin()) return true;

  router.navigate(['/reader']);
  return false;
};

export const reader1Guard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (!auth.isAuthenticated()) {
    router.navigate(['/login']);
    return false;
  }

  if (!auth.isAdmin()) return true;

  router.navigate(['/reader2']);
  return false;
};
