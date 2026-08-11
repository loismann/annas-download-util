import { CanActivateFn, Router } from '@angular/router';
import { inject } from '@angular/core';
import { AuthService } from '../services/auth.service';

/**
 * Reader II is everybody's reader now.
 *
 * <p>This file was the split: while both readers ran, the admin lived on Reader
 * II and everyone else stayed on Reader I. Reader II having proved itself, the
 * split is over — and what is left is one guard that lets anyone signed in read,
 * and one that forwards a stale <c>/reader</c> link to where the reader now
 * lives.</p>
 *
 * <p>A redirect rather than a refusal: the person asked to read, and there is
 * exactly one place to do it, so the app takes them there instead of arguing.
 * Reader I's code is still on disk and its route still resolves — nothing here
 * deletes it. That is the retirement gate's job, and this file goes with
 * it.</p>
 */
export const reader2Guard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (auth.isAuthenticated()) return true;

  router.navigate(['/login']);
  return false;
};

/** Nobody's reader any more: every <c>/reader</c> link lands on Reader II. */
export const reader1Guard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  router.navigate([auth.isAuthenticated() ? '/reader2' : '/login']);
  return false;
};
