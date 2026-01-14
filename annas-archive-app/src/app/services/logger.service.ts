import { Injectable, isDevMode } from '@angular/core';

/**
 * Centralized logging service that wraps console methods.
 * In production mode, all logging is suppressed to improve performance and security.
 * In development mode, logs are output with optional prefixes for easy filtering.
 */
@Injectable({
  providedIn: 'root'
})
export class LoggerService {
  private readonly isEnabled: boolean;

  constructor() {
    this.isEnabled = isDevMode();
  }

  /**
   * Log informational messages (only in dev mode)
   */
  log(message: string, ...args: unknown[]): void {
    if (this.isEnabled) {
      console.log(message, ...args);
    }
  }

  /**
   * Log informational messages with a prefix tag (only in dev mode)
   */
  info(prefix: string, message: string, ...args: unknown[]): void {
    if (this.isEnabled) {
      console.log(`[${prefix}] ${message}`, ...args);
    }
  }

  /**
   * Log warning messages (only in dev mode)
   */
  warn(message: string, ...args: unknown[]): void {
    if (this.isEnabled) {
      console.warn(message, ...args);
    }
  }

  /**
   * Log error messages (always enabled - errors should be visible in production for debugging)
   */
  error(message: string, ...args: unknown[]): void {
    console.error(message, ...args);
  }

  /**
   * Log debug messages (only in dev mode)
   */
  debug(message: string, ...args: unknown[]): void {
    if (this.isEnabled) {
      console.debug(message, ...args);
    }
  }

  /**
   * Group related logs together (only in dev mode)
   */
  group(label: string): void {
    if (this.isEnabled) {
      console.group(label);
    }
  }

  /**
   * End a log group (only in dev mode)
   */
  groupEnd(): void {
    if (this.isEnabled) {
      console.groupEnd();
    }
  }

  /**
   * Log a table (only in dev mode)
   */
  table(data: unknown): void {
    if (this.isEnabled) {
      console.table(data);
    }
  }
}
