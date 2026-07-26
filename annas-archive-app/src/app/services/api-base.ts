/**
 * Resolves the API origin for HTTP calls. In production the Angular build is
 * served by the API itself (same origin), so this is empty and URLs are
 * relative; in local dev (`ng serve` on :4200) the API runs on its own port.
 * Replaces the identical ternary previously copy-pasted into every service.
 */
export function apiBase(port = 5001): string {
  return typeof window !== 'undefined' && window.location.hostname === 'localhost'
    ? `http://localhost:${port}`
    : '';
}
