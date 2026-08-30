import { HttpErrorResponse } from '@angular/common/http';

/**
 * What to tell the reader when a send fails.
 *
 * The API distinguishes three failures that mean genuinely different things —
 * **404** the book is in no catalogue we can download from, **429** the reader's
 * own Anna's allowance is spent, **502** the mirrors could not be reached — and
 * carries a sentence explaining each. The book card rendered all three as the
 * same **Retry**, which is actively misleading: two of them are worth retrying
 * and one is not.
 *
 * The server's own sentence is preferred over anything written here. It knows
 * which catalogue was asked and why it refused; this file only has a status
 * code. The fallbacks exist for the cases where the body is missing or
 * wrong-shaped — a proxy timeout, an interceptor that replaced the body — and
 * must never be more specific than the status actually justifies.
 */
interface MessageCarrier {
  message?: unknown;
}

/**
 * Accepts `unknown` for the same reason `readDownloadQuota` does: the failure
 * arrives as whatever the interceptor produced, and a wrong-shaped body must
 * still yield something readable rather than throw inside an error handler.
 */
export function sendFailureMessage(source: unknown, label: string): string {
  const fromServer = serverMessage(source);
  if (fromServer) return `${label}: ${fromServer}`;

  const status = source instanceof HttpErrorResponse ? source.status : 0;

  switch (status) {
    case 404:
      return `${label}: this book could not be found in any catalogue we can download from.`;
    case 429:
      return `${label}: the daily download allowance is used up. It refreshes on its own.`;
    case 502:
    case 503:
      return `${label}: the download mirrors could not be reached. Please try again shortly.`;
    case 401:
    case 403:
      return `${label}: you are not signed in, or not allowed to do that.`;
    case 0:
      return `${label}: could not reach the server. Please check your connection.`;
    default:
      return `${label} failed (${status}).`;
  }
}

/** The server's sentence, when it sent one that is actually a sentence. */
function serverMessage(source: unknown): string | null {
  const body = source instanceof HttpErrorResponse ? source.error : source;
  const message = (body as MessageCarrier | null | undefined)?.message;

  if (typeof message !== 'string') return null;

  const trimmed = message.trim();
  return trimmed.length > 0 ? trimmed : null;
}
