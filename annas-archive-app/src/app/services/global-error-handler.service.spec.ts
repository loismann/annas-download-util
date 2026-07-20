import { GlobalErrorHandler, ApiError } from './global-error-handler.service';
import { LoggerService } from './logger.service';
import { NgZone } from '@angular/core';

describe('GlobalErrorHandler', () => {
  let handler: GlobalErrorHandler;
  let mockLogger: jasmine.SpyObj<LoggerService>;
  let mockNgZone: jasmine.SpyObj<NgZone>;

  beforeEach(() => {
    mockLogger = jasmine.createSpyObj('LoggerService', ['error', 'debug']);
    mockNgZone = jasmine.createSpyObj('NgZone', ['runOutsideAngular']);
    // Make runOutsideAngular execute the callback immediately
    mockNgZone.runOutsideAngular.and.callFake(<T>(fn: () => T): T => fn());

    // Create handler directly without TestBed to avoid NgZone injection issues
    handler = new GlobalErrorHandler(mockLogger, mockNgZone);
  });

  describe('handleError', () => {
    it('should run outside Angular zone', () => {
      handler.handleError(new Error('test'));
      expect(mockNgZone.runOutsideAngular).toHaveBeenCalled();
    });

    describe('with Error instances', () => {
      it('should log Error with message and type', () => {
        const error = new Error('Test error message');
        handler.handleError(error);

        expect(mockLogger.error).toHaveBeenCalledWith(
          '[GlobalErrorHandler] Unhandled error:',
          { message: 'Test error message', type: 'Error' }
        );
      });

      it('should log custom Error types', () => {
        class CustomError extends Error {
          constructor(message: string) {
            super(message);
            this.name = 'CustomError';
          }
        }
        const error = new CustomError('Custom error');
        handler.handleError(error);

        expect(mockLogger.error).toHaveBeenCalledWith(
          '[GlobalErrorHandler] Unhandled error:',
          { message: 'Custom error', type: 'CustomError' }
        );
      });

      it('should log stack trace in debug', () => {
        const error = new Error('Test with stack');
        handler.handleError(error);

        expect(mockLogger.debug).toHaveBeenCalledWith(
          '[GlobalErrorHandler] Stack trace:',
          jasmine.any(String)
        );
      });

      it('should handle TypeError', () => {
        const error = new TypeError('Cannot read property of undefined');
        handler.handleError(error);

        expect(mockLogger.error).toHaveBeenCalledWith(
          '[GlobalErrorHandler] Unhandled error:',
          { message: 'Cannot read property of undefined', type: 'TypeError' }
        );
      });

      it('should handle RangeError', () => {
        const error = new RangeError('Invalid array length');
        handler.handleError(error);

        expect(mockLogger.error).toHaveBeenCalledWith(
          '[GlobalErrorHandler] Unhandled error:',
          { message: 'Invalid array length', type: 'RangeError' }
        );
      });
    });

    describe('with ApiError objects', () => {
      it('should extract error message from ApiError', () => {
        const apiError: ApiError = { error: 'API request failed' };
        handler.handleError(apiError);

        expect(mockLogger.error).toHaveBeenCalledWith(
          '[GlobalErrorHandler] Unhandled error:',
          {
            message: 'API request failed',
            type: 'ApiError',
            code: undefined,
            details: undefined
          }
        );
      });

      it('should extract errorCode from ApiError', () => {
        const apiError: ApiError = {
          error: 'Validation failed',
          errorCode: 'VALIDATION_ERROR'
        };
        handler.handleError(apiError);

        expect(mockLogger.error).toHaveBeenCalledWith(
          '[GlobalErrorHandler] Unhandled error:',
          {
            message: 'Validation failed',
            type: 'ApiError',
            code: 'VALIDATION_ERROR',
            details: undefined
          }
        );
      });

      it('should extract details from ApiError', () => {
        const apiError: ApiError = {
          error: 'Validation failed',
          errorCode: 'VALIDATION_ERROR',
          details: {
            email: ['Invalid email format'],
            password: ['Must be at least 8 characters']
          }
        };
        handler.handleError(apiError);

        expect(mockLogger.error).toHaveBeenCalledWith(
          '[GlobalErrorHandler] Unhandled error:',
          {
            message: 'Validation failed',
            type: 'ApiError',
            code: 'VALIDATION_ERROR',
            details: {
              email: ['Invalid email format'],
              password: ['Must be at least 8 characters']
            }
          }
        );
      });

      it('should not log stack trace for ApiError (no stack property)', () => {
        const apiError: ApiError = { error: 'API error' };
        handler.handleError(apiError);

        expect(mockLogger.debug).not.toHaveBeenCalled();
      });
    });

    describe('with string errors', () => {
      it('should handle string error', () => {
        handler.handleError('Simple string error');

        expect(mockLogger.error).toHaveBeenCalledWith(
          '[GlobalErrorHandler] Unhandled error:',
          { message: 'Simple string error', type: 'StringError' }
        );
      });

      it('should handle empty string error', () => {
        handler.handleError('');

        expect(mockLogger.error).toHaveBeenCalledWith(
          '[GlobalErrorHandler] Unhandled error:',
          { message: '', type: 'StringError' }
        );
      });
    });

    describe('with unknown error types', () => {
      it('should handle null error', () => {
        handler.handleError(null);

        expect(mockLogger.error).toHaveBeenCalledWith(
          '[GlobalErrorHandler] Unhandled error:',
          { message: 'An unknown error occurred', type: 'UnknownError' }
        );
      });

      it('should handle undefined error', () => {
        handler.handleError(undefined);

        expect(mockLogger.error).toHaveBeenCalledWith(
          '[GlobalErrorHandler] Unhandled error:',
          { message: 'An unknown error occurred', type: 'UnknownError' }
        );
      });

      it('should handle number error', () => {
        handler.handleError(42);

        expect(mockLogger.error).toHaveBeenCalledWith(
          '[GlobalErrorHandler] Unhandled error:',
          { message: 'An unknown error occurred', type: 'UnknownError' }
        );
      });

      it('should handle object without error property', () => {
        handler.handleError({ someProperty: 'value' });

        expect(mockLogger.error).toHaveBeenCalledWith(
          '[GlobalErrorHandler] Unhandled error:',
          { message: 'An unknown error occurred', type: 'UnknownError' }
        );
      });

      it('should handle object with non-string error property', () => {
        handler.handleError({ error: 123 });

        expect(mockLogger.error).toHaveBeenCalledWith(
          '[GlobalErrorHandler] Unhandled error:',
          { message: 'An unknown error occurred', type: 'UnknownError' }
        );
      });

      it('should handle array error', () => {
        handler.handleError([1, 2, 3]);

        expect(mockLogger.error).toHaveBeenCalledWith(
          '[GlobalErrorHandler] Unhandled error:',
          { message: 'An unknown error occurred', type: 'UnknownError' }
        );
      });

      it('should handle boolean error', () => {
        handler.handleError(false);

        expect(mockLogger.error).toHaveBeenCalledWith(
          '[GlobalErrorHandler] Unhandled error:',
          { message: 'An unknown error occurred', type: 'UnknownError' }
        );
      });
    });

    describe('edge cases', () => {
      it('should handle Error with no stack trace', () => {
        const error = new Error('No stack');
        // Manually remove stack
        delete (error as any).stack;

        handler.handleError(error);

        expect(mockLogger.error).toHaveBeenCalled();
        expect(mockLogger.debug).not.toHaveBeenCalled();
      });

      it('should handle Error with empty stack', () => {
        const error = new Error('Empty stack');
        error.stack = '';

        handler.handleError(error);

        expect(mockLogger.error).toHaveBeenCalled();
        expect(mockLogger.debug).not.toHaveBeenCalled();
      });

      it('should handle ApiError with empty details object', () => {
        const apiError: ApiError = {
          error: 'Error with empty details',
          details: {}
        };
        handler.handleError(apiError);

        expect(mockLogger.error).toHaveBeenCalledWith(
          '[GlobalErrorHandler] Unhandled error:',
          {
            message: 'Error with empty details',
            type: 'ApiError',
            code: undefined,
            details: {}
          }
        );
      });
    });
  });
});
