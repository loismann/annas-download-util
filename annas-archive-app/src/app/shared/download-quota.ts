import { HttpErrorResponse } from '@angular/common/http';

/**
 * Anna's Archive allows a limited number of fast downloads per day, and the
 * server reports what is left in `accountFastInfo`.
 *
 * The subtlety this exists for: **a failed download can still have consumed a
 * slot**, so the counter has to be read from failures too — not just successes.
 * The download endpoints therefore return a real error status (429 for a rate
 * limit, 502 when Anna's Archive could not produce the file) while keeping
 * `accountFastInfo` in the body. Before that they answered 200 with
 * `success: false`, which kept the counter working but made every download
 * failure invisible to Serilog, Seq, and anything else that reads status codes.
 *
 * So the counter now has to be pulled from two differently-shaped things — a
 * response body and an `HttpErrorResponse.error` — and getting that wrong fails
 * silently: the tile still turns red, the number just quietly stops moving. One
 * function, one place to get it right, and a test that fails if it stops working.
 */
export interface DownloadQuota {
  downloadsLeft: number;
  downloadsPerDay: number;
}

interface QuotaCarrier {
  accountFastInfo?: DownloadQuota | null;
}

/**
 * Pulls the quota out of a success body or a failure, or returns null when
 * neither carries one.
 *
 * Accepts `unknown` deliberately: the failure case arrives as whatever the
 * interceptor produced, and a wrong-shaped body must return null rather than
 * throw — a malformed response should leave the counter alone, never break the
 * handler that was reporting the error.
 */
export function readDownloadQuota(source: unknown): DownloadQuota | null {
  const body = source instanceof HttpErrorResponse ? source.error : source;
  const quota = (body as QuotaCarrier | null | undefined)?.accountFastInfo;

  if (!quota) return null;
  if (typeof quota.downloadsLeft !== 'number' || typeof quota.downloadsPerDay !== 'number') return null;

  return { downloadsLeft: quota.downloadsLeft, downloadsPerDay: quota.downloadsPerDay };
}
