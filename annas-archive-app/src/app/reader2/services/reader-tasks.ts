import { Injectable, signal } from '@angular/core';
import { StreamEvent } from './reader2-api.service';
import { ProgressStep } from '../reader2.models';

/** What the reader is waiting for, if anything. */
export interface Busy {
  what: string;
  step: ProgressStep | null;
}

/**
 * One busy flag and one error, shared by every reader store.
 *
 * <p>Extracted rather than repeated because the reader shows the <i>reader</i>
 * one banner at a time, not one per feature — three stores each with their own
 * `busy` signal would render three spinners for what is, to the person reading,
 * a single wait. Sharing the signal makes that structurally true instead of
 * something the shell has to remember to co-ordinate.</p>
 *
 * <p>Provided by the shell, not in root: this is per-reader state, and a
 * root-scoped instance would carry one book's error into the next.</p>
 */
@Injectable()
export class ReaderTasks {
  readonly busy = signal<Busy | null>(null);
  readonly error = signal<string | null>(null);

  /**
   * Runs work with the busy flag set and one place for the failure to land.
   * Returns null when it failed, so a caller can tell without a second flag.
   */
  async run<T>(what: string, work: () => Promise<T>): Promise<T | null> {
    this.busy.set({ what, step: null });
    this.error.set(null);

    try {
      return await work();
    } catch (error: unknown) {
      this.error.set(describe(error));
      return null;
    } finally {
      this.busy.set(null);
    }
  }

  /** As above, for a stream: progress lands on the same busy signal. */
  stream<T>(
    what: string,
    stream: { subscribe: (observer: StreamObserver<T>) => unknown },
    done: (value: T) => void
  ): Promise<void> {
    this.busy.set({ what, step: null });
    this.error.set(null);

    return new Promise<void>(resolve => {
      stream.subscribe({
        next: (event: StreamEvent<T>) => {
          if (event.kind === 'progress') this.busy.set({ what, step: event.step });
          if (event.kind === 'result') done(event.value);
          if (event.kind === 'error') this.error.set(event.message);
        },
        error: (error: unknown) => {
          this.error.set(describe(error));
          this.busy.set(null);
          resolve();
        },
        complete: () => {
          this.busy.set(null);
          resolve();
        }
      });
    });
  }

}

/**
 * Work that is not worth an error banner — saving a position, saving a
 * preference.
 *
 * <p>A reader who cannot save where they are should still be able to read;
 * surfacing it would put a red box over the text for something that will
 * succeed on the next page turn. Named rather than repeated as a bare
 * `.catch(() => undefined)`, so the swallow reads as a decision.</p>
 */
export async function quietly(work: () => Promise<unknown>): Promise<void> {
  await work().catch(() => undefined);
}

interface StreamObserver<T> {
  next: (event: StreamEvent<T>) => void;
  error: (error: unknown) => void;
  complete: () => void;
}

/** The server's sentence when there is one, rather than a status code. */
export function describe(error: unknown): string {
  const body = (error as { error?: { error?: string; detail?: string } } | null)?.error;
  return body?.error ?? body?.detail ?? 'Something went wrong. Try again.';
}
