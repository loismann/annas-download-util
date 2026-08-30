import { HttpErrorResponse } from '@angular/common/http';
import { sendFailureMessage } from './send-failure-message';

/**
 * The three failures the API takes care to distinguish must not collapse back
 * into one message. They did: the card rendered "Retry" for all of them, so a
 * book that is simply not in any catalogue looked identical to a spent quota.
 */
describe('sendFailureMessage', () => {
  const err = (status: number, body?: unknown) =>
    new HttpErrorResponse({ status, error: body, url: '/api/anna/book/abc/send-to-boox' });

  it("prefers the server's own sentence over anything local", () => {
    const message = sendFailureMessage(
      err(404, { message: "This book is not available from Anna's Archive." }),
      'Send-to-Boox');

    expect(message).toContain("not available from Anna's Archive");
    expect(message).toContain('Send-to-Boox');
  });

  it('names the operation so two buttons failing are told apart', () => {
    expect(sendFailureMessage(err(502), "Send-to-Dad's-Kindle")).toContain("Send-to-Dad's-Kindle");
  });

  /** The point of the whole change: three statuses, three different sentences. */
  it('gives each failure kind a distinct message', () => {
    const notFound = sendFailureMessage(err(404), 'Send');
    const rateLimited = sendFailureMessage(err(429), 'Send');
    const unreachable = sendFailureMessage(err(502), 'Send');

    expect(new Set([notFound, rateLimited, unreachable]).size).toBe(3);
  });

  it('says a spent allowance refreshes on its own, because that one is worth waiting out', () => {
    expect(sendFailureMessage(err(429), 'Send')).toContain('refreshes');
  });

  it('does not suggest retrying a book no catalogue has', () => {
    expect(sendFailureMessage(err(404), 'Send')).not.toContain('try again');
  });

  it('suggests retrying when the mirrors were unreachable', () => {
    expect(sendFailureMessage(err(502), 'Send')).toContain('try again');
  });

  it('treats a status of 0 as a connection problem, not a server refusal', () => {
    expect(sendFailureMessage(err(0), 'Send')).toContain('connection');
  });

  /**
   * A wrong-shaped body must not throw: this runs inside an error handler, and
   * throwing there loses the failure it was reporting.
   */
  [undefined, null, 'a string', 42, { message: null }, { message: '   ' }, { nope: true }]
    .forEach(body => {
      it(`falls back to the status when the body is ${JSON.stringify(body)}`, () => {
        const message = sendFailureMessage(err(429, body), 'Send');

        expect(message).toContain('allowance');
      });
    });

  it('never throws on something that is not an HttpErrorResponse at all', () => {
    expect(() => sendFailureMessage('not an error', 'Send')).not.toThrow();
    expect(sendFailureMessage(undefined, 'Send')).toContain('Send');
  });

  it('still reports an unmapped status rather than saying nothing', () => {
    expect(sendFailureMessage(err(418), 'Send')).toContain('418');
  });
});
