import { Injectable, OnDestroy } from '@angular/core';
import { ReaderTextUtilsService } from './reader-text-utils.service';

/**
 * Configuration for page size calculation.
 */
export interface PaginationConfig {
  fontSize: number;
  containerWidth: number;
  containerHeight: number;
}

/**
 * Result of page size calculation.
 */
export interface PageSizeResult {
  pageSize: number;
  cacheKey: string;
}

/**
 * Service for calculating optimal page sizes based on container dimensions
 * and text content. Uses binary search and DOM measurement for accuracy.
 */
@Injectable({
  providedIn: 'root'
})
export class ReaderPaginationService implements OnDestroy {
  private measurementEl: HTMLElement | null = null;
  private cachedPageSize: number | null = null;
  private pageSizeCacheKey: string | null = null;

  constructor(private textUtils: ReaderTextUtilsService) {}

  ngOnDestroy(): void {
    this.cleanup();
  }

  /**
   * Cleans up the measurement element from the DOM.
   */
  cleanup(): void {
    if (this.measurementEl && this.measurementEl.parentNode) {
      this.measurementEl.parentNode.removeChild(this.measurementEl);
      this.measurementEl = null;
    }
    this.cachedPageSize = null;
    this.pageSizeCacheKey = null;
  }

  /**
   * Calculates the optimal page size for the given container and content.
   * Uses caching to avoid recalculation when dimensions haven't changed.
   *
   * @param textContent The full text content to paginate
   * @param container The DOM element containing the text
   * @param fontSize The current font size in pixels
   * @returns The calculated page size result
   */
  calculatePageSize(
    textContent: string,
    container: HTMLElement,
    fontSize: number
  ): PageSizeResult {
    if (!container || !textContent) {
      return { pageSize: 200, cacheKey: '' };
    }

    const cacheKey = `${container.clientWidth}x${container.clientHeight}@${fontSize}`;

    // Return cached result if dimensions haven't changed
    if (this.pageSizeCacheKey === cacheKey && this.cachedPageSize !== null) {
      return { pageSize: this.cachedPageSize, cacheKey };
    }

    // Setup measurement element
    this.setupMeasurementElement(container);

    // Find optimal page size
    const newSize = this.findOptimalPageSize(textContent, container);

    // Update cache
    this.cachedPageSize = newSize;
    this.pageSizeCacheKey = cacheKey;

    return { pageSize: newSize, cacheKey };
  }

  /**
   * Invalidates the page size cache, forcing recalculation on next call.
   */
  invalidateCache(): void {
    this.cachedPageSize = null;
    this.pageSizeCacheKey = null;
  }

  /**
   * Gets a quick estimate of page size based on font metrics.
   * Useful for initial rendering before accurate measurement.
   */
  getEstimatedPageSize(container: HTMLElement): number {
    if (!container) return 200;

    const styles = getComputedStyle(container);
    const fontSize = parseFloat(styles.fontSize) || 14;
    const lineHeight = parseFloat(styles.lineHeight) || (fontSize * 1.7);
    const paddingY = parseFloat(styles.paddingTop || '0') + parseFloat(styles.paddingBottom || '0');
    const availableHeight = Math.max(0, container.clientHeight - paddingY);
    const lines = Math.max(3, Math.floor(availableHeight / lineHeight));
    const avgCharWidth = fontSize * 0.6;
    const approxCharsPerLine = Math.max(16, Math.floor(container.clientWidth / avgCharWidth));
    const approxWordsPerLine = Math.max(4, Math.floor(approxCharsPerLine / 6));

    return Math.max(20, Math.floor(lines * approxWordsPerLine));
  }

  /**
   * Calculates total pages for given content and page size.
   */
  calculateTotalPages(wordCount: number, pageSize: number): number {
    if (wordCount <= 0 || pageSize <= 0) return 1;
    return Math.ceil(wordCount / pageSize);
  }

  /**
   * Calculates the current page number (1-indexed).
   */
  calculateCurrentPage(wordOffset: number, pageSize: number): number {
    if (pageSize <= 0) return 1;
    return Math.floor(wordOffset / pageSize) + 1;
  }

  /**
   * Calculates the word offset for a given page number (1-indexed).
   */
  getOffsetForPage(page: number, pageSize: number): number {
    return Math.max(0, (page - 1) * pageSize);
  }

  /**
   * Clamps word offset to valid range.
   */
  clampOffset(wordOffset: number, totalWords: number, pageSize: number): number {
    const totalPages = this.calculateTotalPages(totalWords, pageSize);
    const maxStart = Math.max(0, (totalPages - 1) * pageSize);
    return Math.min(Math.max(0, wordOffset), maxStart);
  }

  /**
   * Sets up the hidden measurement element for text overflow testing.
   */
  private setupMeasurementElement(container: HTMLElement): void {
    if (!this.measurementEl) {
      this.measurementEl = document.createElement('div');
      this.measurementEl.style.cssText = `
        position: absolute;
        left: -9999px;
        top: 0;
        overflow: hidden;
        visibility: hidden;
        pointer-events: none;
      `;
      document.body.appendChild(this.measurementEl);
    }

    // Update dimensions and styles to match container
    const styles = getComputedStyle(container);
    this.measurementEl.style.width = `${container.clientWidth}px`;
    this.measurementEl.style.height = `${container.clientHeight}px`;
    this.measurementEl.style.fontSize = styles.fontSize;
    this.measurementEl.style.fontFamily = styles.fontFamily;
    this.measurementEl.style.lineHeight = styles.lineHeight;
    this.measurementEl.style.padding = styles.padding;
    this.measurementEl.style.boxSizing = 'border-box';
  }

  /**
   * Tests if the given number of words would overflow the container.
   */
  private doesTextOverflow(
    text: string,
    wordCount: number,
    testOffset: number
  ): boolean {
    if (!this.measurementEl || !text) return false;

    const testText = this.textUtils.sliceByWords(text, testOffset, wordCount);
    const html = this.textUtils.escapeHtml(testText).replace(/\n/g, '<br/>');

    this.measurementEl.innerHTML = `<pre style="white-space: pre-wrap; word-wrap: break-word; margin: 0; line-height: inherit;">${html}</pre>`;

    return this.measurementEl.scrollHeight > this.measurementEl.clientHeight;
  }

  /**
   * Uses binary search to find the optimal page size.
   */
  private findOptimalPageSize(text: string, container: HTMLElement): number {
    if (!text) return 200;

    const totalWords = this.textUtils.countWords(text);
    const testOffset = 0; // Use fixed offset for consistent page sizes
    const maxPossible = Math.min(totalWords, 800); // Cap for performance

    if (maxPossible <= 10) return maxPossible;

    // Get estimation for binary search bounds
    const estimate = this.getEstimatedPageSize(container);

    // Binary search with wider range to handle estimate inaccuracy
    let low = Math.max(10, Math.floor(estimate * 0.3));
    let high = Math.min(maxPossible, Math.ceil(estimate * 2.5));
    let result = low;

    // Check if low value overflows - if so, go lower
    if (this.doesTextOverflow(text, low, testOffset)) {
      high = low;
      low = 10;
    }

    while (low <= high) {
      const mid = Math.floor((low + high) / 2);

      if (this.doesTextOverflow(text, mid, testOffset)) {
        high = mid - 1;
      } else {
        result = mid;
        low = mid + 1;
      }
    }

    return Math.max(10, result);
  }
}
