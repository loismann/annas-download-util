import { HttpInterceptorFn, HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { LoggerService } from '../services/logger.service';
import { ApiError } from '../services/global-error-handler.service';

/**
 * HTTP interceptor that handles API errors consistently.
 * Logs errors and transforms them into a consistent format.
 */
export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  const logger = inject(LoggerService);

  return next(req).pipe(
    catchError((error: HttpErrorResponse) => {
      // Extract API error details if available
      let apiError = extractApiError(error);

      // For network errors (status 0), provide a clear connection error message
      if (error.status === 0 && !apiError) {
        apiError = {
          error: 'Cannot connect to server. Please check your connection.',
          errorCode: 'NETWORK_ERROR'
        };
      }

      const errorMessage = apiError?.error ?? error.message ?? 'Unknown error';

      // Log based on error type
      switch (error.status) {
        case 0:
          // Network error or CORS issue
          logger.error(`[HTTP] Network error for ${req.method} ${req.url}:`, errorMessage);
          break;
        case 400:
          // Validation error
          logger.warn(`[HTTP] Validation error for ${req.method} ${req.url}:`, apiError?.details ?? errorMessage);
          break;
        case 401:
          // Unauthorized
          logger.warn(`[HTTP] Unauthorized request to ${req.url}`);
          break;
        case 403:
          // Forbidden
          logger.warn(`[HTTP] Forbidden request to ${req.url}`);
          break;
        case 404:
          // Not found
          logger.warn(`[HTTP] Resource not found: ${req.url}`);
          break;
        case 429:
          // Rate limited
          const retryAfter = apiError?.details?.['retryAfter']?.[0];
          logger.warn(`[HTTP] Rate limited on ${req.url}. Retry after: ${retryAfter ?? 'unknown'}s`);
          break;
        case 500:
        case 502:
        case 503:
        case 504:
          // Server errors
          logger.error(`[HTTP] Server error (${error.status}) for ${req.method} ${req.url}:`, errorMessage);
          break;
        default:
          logger.error(`[HTTP] Error (${error.status}) for ${req.method} ${req.url}:`, errorMessage);
      }

      // Re-throw the original error with enhanced error body
      // This preserves .status for component-level handling while ensuring consistent error format
      const enhancedError = new HttpErrorResponse({
        error: apiError ?? {
          error: errorMessage,
          errorCode: `HTTP_${error.status}`,
          details: { status: [error.status.toString()], statusText: [error.statusText] }
        } as ApiError,
        headers: error.headers,
        status: error.status,
        statusText: error.statusText,
        url: error.url ?? undefined
      });
      return throwError(() => enhancedError);
    })
  );
};

/**
 * Extract API error from HTTP error response.
 */
function extractApiError(error: HttpErrorResponse): ApiError | null {
  const body = error.error;

  // Check if body matches ApiError format
  if (
    body &&
    typeof body === 'object' &&
    'error' in body &&
    typeof body.error === 'string'
  ) {
    return body as ApiError;
  }

  // Try to extract error message from common response formats
  if (body && typeof body === 'object') {
    const message = body['message'] ?? body['Message'] ?? body['title'] ?? body['Title'];
    if (typeof message === 'string') {
      return { error: message };
    }
  }

  // If body is a string, use it as error message
  if (typeof body === 'string' && body.length > 0 && body.length < 500) {
    return { error: body };
  }

  return null;
}
