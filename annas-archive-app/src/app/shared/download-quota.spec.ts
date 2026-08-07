import { HttpErrorResponse } from '@angular/common/http';
import { readDownloadQuota } from './download-quota';

describe('readDownloadQuota', () => {
  const quota = { downloadsLeft: 37, downloadsPerDay: 100 };

  it('reads the quota from a successful response body', () => {
    expect(readDownloadQuota({ success: true, accountFastInfo: quota })).toEqual(quota);
  });

  // The whole reason this function exists. Download failures answer 429 or 502
  // now, and a failed attempt can still have consumed a slot — so the counter
  // has to come out of the error just as reliably as out of a success.
  it('reads the quota out of an HttpErrorResponse', () => {
    const error = new HttpErrorResponse({
      status: 502,
      error: { success: false, message: 'Failed to download book.', accountFastInfo: quota }
    });

    expect(readDownloadQuota(error)).toEqual(quota);
  });

  it('reads the quota out of a rate-limit failure', () => {
    const error = new HttpErrorResponse({
      status: 429,
      error: { success: false, message: '⏱️ Rate limit exceeded.', accountFastInfo: quota }
    });

    expect(readDownloadQuota(error)).toEqual(quota);
  });

  it('returns null when the response carries no quota', () => {
    expect(readDownloadQuota({ success: true })).toBeNull();
    expect(readDownloadQuota({ accountFastInfo: null })).toBeNull();
  });

  it('returns null for a failure that carries no body', () => {
    expect(readDownloadQuota(new HttpErrorResponse({ status: 500 }))).toBeNull();
  });

  // A malformed body must leave the counter alone rather than throw — this runs
  // inside an error handler that is already reporting a failure, and a second
  // exception there would replace a visible error with an invisible one.
  it('returns null rather than throwing on a wrong-shaped body', () => {
    expect(readDownloadQuota(null)).toBeNull();
    expect(readDownloadQuota(undefined)).toBeNull();
    expect(readDownloadQuota('a string')).toBeNull();
    expect(readDownloadQuota({ accountFastInfo: 'nonsense' })).toBeNull();
    expect(readDownloadQuota({ accountFastInfo: { downloadsLeft: '37' } })).toBeNull();
    expect(readDownloadQuota(new HttpErrorResponse({ status: 502, error: 'plain text' }))).toBeNull();
  });

  it('requires both numbers, not just one', () => {
    expect(readDownloadQuota({ accountFastInfo: { downloadsLeft: 5 } })).toBeNull();
    expect(readDownloadQuota({ accountFastInfo: { downloadsPerDay: 100 } })).toBeNull();
  });

  it('accepts a genuine zero', () => {
    expect(readDownloadQuota({ accountFastInfo: { downloadsLeft: 0, downloadsPerDay: 100 } }))
      .toEqual({ downloadsLeft: 0, downloadsPerDay: 100 });
  });
});
