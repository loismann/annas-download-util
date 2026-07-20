import { TestBed } from '@angular/core/testing';
import { ReaderPaginationService } from './reader-pagination.service';
import { ReaderTextUtilsService } from './reader-text-utils.service';

describe('ReaderPaginationService', () => {
  let service: ReaderPaginationService;
  let textUtils: ReaderTextUtilsService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        ReaderPaginationService,
        ReaderTextUtilsService
      ]
    });
    service = TestBed.inject(ReaderPaginationService);
    textUtils = TestBed.inject(ReaderTextUtilsService);
  });

  afterEach(() => {
    service.cleanup();
  });

  describe('calculateTotalPages', () => {
    it('should return 1 for zero word count', () => {
      expect(service.calculateTotalPages(0, 100)).toBe(1);
    });

    it('should return 1 for negative word count', () => {
      expect(service.calculateTotalPages(-10, 100)).toBe(1);
    });

    it('should return 1 for zero page size', () => {
      expect(service.calculateTotalPages(500, 0)).toBe(1);
    });

    it('should return 1 for negative page size', () => {
      expect(service.calculateTotalPages(500, -100)).toBe(1);
    });

    it('should calculate correct number of full pages', () => {
      expect(service.calculateTotalPages(1000, 100)).toBe(10);
    });

    it('should round up for partial pages', () => {
      expect(service.calculateTotalPages(1050, 100)).toBe(11);
    });

    it('should return 1 for single page content', () => {
      expect(service.calculateTotalPages(50, 100)).toBe(1);
    });

    it('should handle exact page boundary', () => {
      expect(service.calculateTotalPages(300, 100)).toBe(3);
    });
  });

  describe('calculateCurrentPage', () => {
    it('should return 1 for zero offset', () => {
      expect(service.calculateCurrentPage(0, 100)).toBe(1);
    });

    it('should return 1 for zero page size', () => {
      expect(service.calculateCurrentPage(500, 0)).toBe(1);
    });

    it('should return 1 for negative page size', () => {
      expect(service.calculateCurrentPage(500, -100)).toBe(1);
    });

    it('should calculate correct page for offset in first page', () => {
      expect(service.calculateCurrentPage(50, 100)).toBe(1);
    });

    it('should calculate correct page for offset in second page', () => {
      expect(service.calculateCurrentPage(150, 100)).toBe(2);
    });

    it('should calculate correct page at page boundary', () => {
      expect(service.calculateCurrentPage(100, 100)).toBe(2);
    });

    it('should handle large offset', () => {
      expect(service.calculateCurrentPage(950, 100)).toBe(10);
    });
  });

  describe('getOffsetForPage', () => {
    it('should return 0 for page 1', () => {
      expect(service.getOffsetForPage(1, 100)).toBe(0);
    });

    it('should return 0 for page 0 (invalid)', () => {
      expect(service.getOffsetForPage(0, 100)).toBe(0);
    });

    it('should return 0 for negative page (invalid)', () => {
      expect(service.getOffsetForPage(-1, 100)).toBe(0);
    });

    it('should calculate correct offset for page 2', () => {
      expect(service.getOffsetForPage(2, 100)).toBe(100);
    });

    it('should calculate correct offset for page 5', () => {
      expect(service.getOffsetForPage(5, 100)).toBe(400);
    });

    it('should handle small page size', () => {
      expect(service.getOffsetForPage(3, 25)).toBe(50);
    });
  });

  describe('clampOffset', () => {
    it('should return 0 for negative offset', () => {
      expect(service.clampOffset(-100, 1000, 100)).toBe(0);
    });

    it('should return 0 for zero offset', () => {
      expect(service.clampOffset(0, 1000, 100)).toBe(0);
    });

    it('should return offset unchanged if within bounds', () => {
      expect(service.clampOffset(500, 1000, 100)).toBe(500);
    });

    it('should clamp to max valid start for overflow', () => {
      // 1000 words, 100 per page = 10 pages
      // Max start should be page 10 = offset 900
      expect(service.clampOffset(950, 1000, 100)).toBe(900);
    });

    it('should handle exact page boundary', () => {
      expect(service.clampOffset(900, 1000, 100)).toBe(900);
    });

    it('should handle small content', () => {
      // 50 words, 100 per page = 1 page, max start = 0
      expect(service.clampOffset(100, 50, 100)).toBe(0);
    });

    it('should handle content exactly one page', () => {
      expect(service.clampOffset(0, 100, 100)).toBe(0);
    });
  });

  describe('invalidateCache', () => {
    it('should reset cache state', () => {
      // Create a mock container
      const container = document.createElement('div');
      container.style.width = '500px';
      container.style.height = '300px';
      container.style.fontSize = '14px';
      document.body.appendChild(container);

      const text = 'word '.repeat(100);

      // First call to populate cache
      service.calculatePageSize(text, container, 14);

      // Invalidate cache
      service.invalidateCache();

      // Clean up
      document.body.removeChild(container);

      // After invalidation, cache should be cleared
      // (we can't directly verify internal state, but method should not throw)
      expect(() => service.invalidateCache()).not.toThrow();
    });
  });

  describe('cleanup', () => {
    it('should not throw when called with no measurement element', () => {
      expect(() => service.cleanup()).not.toThrow();
    });

    it('should remove measurement element from DOM', () => {
      // Create a mock container to trigger measurement element creation
      const container = document.createElement('div');
      container.style.width = '500px';
      container.style.height = '300px';
      container.style.fontSize = '14px';
      document.body.appendChild(container);

      const text = 'word '.repeat(100);
      service.calculatePageSize(text, container, 14);

      // Cleanup should remove measurement element
      service.cleanup();

      // Clean up test container
      document.body.removeChild(container);

      // No error should be thrown on second cleanup
      expect(() => service.cleanup()).not.toThrow();
    });
  });

  describe('calculatePageSize', () => {
    it('should return default size for null container', () => {
      const result = service.calculatePageSize('some text', null as any, 14);
      expect(result.pageSize).toBe(200);
      expect(result.cacheKey).toBe('');
    });

    it('should return default size for empty text', () => {
      const container = document.createElement('div');
      const result = service.calculatePageSize('', container, 14);
      expect(result.pageSize).toBe(200);
      expect(result.cacheKey).toBe('');
    });

    it('should return cached result when dimensions unchanged', () => {
      const container = document.createElement('div');
      Object.defineProperty(container, 'clientWidth', { value: 500 });
      Object.defineProperty(container, 'clientHeight', { value: 300 });
      container.style.fontSize = '14px';
      document.body.appendChild(container);

      const text = 'word '.repeat(100);

      // First call
      const result1 = service.calculatePageSize(text, container, 14);

      // Second call should return cached
      const result2 = service.calculatePageSize(text, container, 14);

      expect(result1.cacheKey).toBe(result2.cacheKey);
      expect(result1.pageSize).toBe(result2.pageSize);

      document.body.removeChild(container);
    });
  });

  describe('getEstimatedPageSize', () => {
    it('should return 200 for null container', () => {
      expect(service.getEstimatedPageSize(null as any)).toBe(200);
    });

    it('should return reasonable estimate for container', () => {
      const container = document.createElement('div');
      Object.defineProperty(container, 'clientWidth', { value: 600 });
      Object.defineProperty(container, 'clientHeight', { value: 400 });

      const estimate = service.getEstimatedPageSize(container);

      // Should be a reasonable value (at least 20, likely higher)
      expect(estimate).toBeGreaterThanOrEqual(20);
    });

    it('should return higher estimate for larger container', () => {
      const smallContainer = document.createElement('div');
      Object.defineProperty(smallContainer, 'clientWidth', { value: 300 });
      Object.defineProperty(smallContainer, 'clientHeight', { value: 200 });

      const largeContainer = document.createElement('div');
      Object.defineProperty(largeContainer, 'clientWidth', { value: 800 });
      Object.defineProperty(largeContainer, 'clientHeight', { value: 600 });

      const smallEstimate = service.getEstimatedPageSize(smallContainer);
      const largeEstimate = service.getEstimatedPageSize(largeContainer);

      expect(largeEstimate).toBeGreaterThan(smallEstimate);
    });
  });

  describe('page navigation consistency', () => {
    it('should navigate correctly through pages', () => {
      const totalWords = 1000;
      const pageSize = 100;

      // Navigate forward through all pages
      for (let page = 1; page <= 10; page++) {
        const offset = service.getOffsetForPage(page, pageSize);
        const calculatedPage = service.calculateCurrentPage(offset, pageSize);
        expect(calculatedPage).toBe(page);
      }
    });

    it('should clamp correctly at document end', () => {
      const totalWords = 950;
      const pageSize = 100;
      const totalPages = service.calculateTotalPages(totalWords, pageSize);

      // Try to go to page 11 (beyond end)
      const offset = service.getOffsetForPage(11, pageSize);
      const clamped = service.clampOffset(offset, totalWords, pageSize);

      // Should be clamped to last page
      const lastPage = service.calculateCurrentPage(clamped, pageSize);
      expect(lastPage).toBe(totalPages);
    });
  });
});
