import { Injectable } from '@angular/core';
import { BehaviorSubject } from 'rxjs';

/** Admin-only "view as" state for testing the real Date Night voting/scheduling
 * UI as Mom or Dad from Paul's own session. In-memory only — resets on reload,
 * which is the right default for a testing aid rather than a permanent identity
 * switch. Only ever set from within an admin's own browser tab; Mom/Dad's real
 * sessions never touch this service. See date-night-impersonation.interceptor.ts
 * for how it reaches the backend. */
@Injectable({ providedIn: 'root' })
export class DateNightImpersonationService {
  private readonly subject = new BehaviorSubject<'Mom' | 'Dad' | null>(null);
  readonly impersonating$ = this.subject.asObservable();

  current(): 'Mom' | 'Dad' | null {
    return this.subject.value;
  }

  set(person: 'Mom' | 'Dad' | null): void {
    this.subject.next(person);
  }
}
