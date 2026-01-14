import { ErrorHandler, Injectable, NgZone } from '@angular/core';
import { LoggerService } from './logger.service';

/**
 * API Error response format matching backend ErrorResponse.
 */
export interface ApiError {
  error: string;
  errorCode?: string;
  details?: Record<string, string[]>;
}

/**
 * Global error handler that catches unhandled errors in the application.
 * Logs errors appropriately and can trigger user notifications.
 */
@Injectable()
export class GlobalErrorHandler implements ErrorHandler {
  constructor(
    private logger: LoggerService,
    private zone: NgZone
  ) {}

  handleError(error: unknown): void {
    // Run outside Angular zone to avoid triggering change detection
    this.zone.runOutsideAngular(() => {
      // Extract error details
      const errorDetails = this.extractErrorDetails(error);

      // Log the error
      this.logger.error('[GlobalErrorHandler] Unhandled error:', errorDetails);

      // Log stack trace in development
      if (error instanceof Error && error.stack) {
        this.logger.debug('[GlobalErrorHandler] Stack trace:', error.stack);
      }
    });
  }

  private extractErrorDetails(error: unknown): {
    message: string;
    type: string;
    code?: string;
    details?: Record<string, string[]>;
  } {
    if (error instanceof Error) {
      return {
        message: error.message,
        type: error.name
      };
    }

    if (this.isApiError(error)) {
      return {
        message: error.error,
        type: 'ApiError',
        code: error.errorCode,
        details: error.details
      };
    }

    if (typeof error === 'string') {
      return {
        message: error,
        type: 'StringError'
      };
    }

    return {
      message: 'An unknown error occurred',
      type: 'UnknownError'
    };
  }

  private isApiError(error: unknown): error is ApiError {
    return (
      typeof error === 'object' &&
      error !== null &&
      'error' in error &&
      typeof (error as ApiError).error === 'string'
    );
  }
}
