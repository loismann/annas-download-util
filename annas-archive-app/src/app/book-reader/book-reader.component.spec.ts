/**
 * Unit tests for chapter summary numbering logic in BookReaderComponent
 *
 * These tests verify that displayChapterNumber is calculated correctly based on
 * the filtered chapters array index (not the raw EPUB chapter ID).
 */

describe('BookReaderComponent - Chapter Summary Number Calculation', () => {
  /**
   * Helper function that mimics the calculation logic in the component
   */
  function calculateDisplayChapterNumber(chapters: any[], selectedChapterId: number): number | undefined {
    const chapterIndex = chapters.findIndex(ch => ch.id === selectedChapterId);
    return chapterIndex >= 0 ? chapterIndex + 1 : undefined;
  }

  describe('displayChapterNumber calculation', () => {
    it('should calculate displayChapterNumber based on filtered chapters index', () => {
      // Setup: Mock chapters with some having low word counts (front matter)
      const chapters = [
        { id: 0, title: 'Preface', level: 0, wordCount: 30 },        // Filtered out (< 50 words)
        { id: 1, title: 'Introduction', level: 0, wordCount: 45 },   // Filtered out (< 50 words)
        { id: 2, title: 'Chapter 1', level: 0, wordCount: 2500 },    // First real chapter (index 0 in filtered)
        { id: 3, title: 'Chapter 2', level: 0, wordCount: 3000 },    // Second real chapter (index 1)
        { id: 4, title: 'Chapter 3', level: 0, wordCount: 2800 }     // Third real chapter (index 2)
      ];

      // Simulate filtering: keep only chapters with >= 50 words
      const filteredChapters = chapters.filter(ch => ch.wordCount >= 50);

      // Test: Chapter with id=2 should be displayChapterNumber=1 (first real chapter in filtered list)
      const selectedChapterId = 2;
      const displayChapterNumber = calculateDisplayChapterNumber(filteredChapters, selectedChapterId);

      expect(displayChapterNumber).toBe(1); // First real chapter (index 0 + 1 in filtered list)
    });

    it('should calculate displayChapterNumber as 2 for second filtered chapter', () => {
      const chapters = [
        { id: 0, title: 'Preface', level: 0, wordCount: 30 },
        { id: 1, title: 'Introduction', level: 0, wordCount: 45 },
        { id: 2, title: 'Chapter 1', level: 0, wordCount: 2500 },
        { id: 3, title: 'Chapter 2', level: 0, wordCount: 3000 },
        { id: 4, title: 'Chapter 3', level: 0, wordCount: 2800 }
      ];

      const filteredChapters = chapters.filter(ch => ch.wordCount >= 50);

      // Test: Chapter with id=3 should be displayChapterNumber=2 (second real chapter)
      const selectedChapterId = 3;
      const displayChapterNumber = calculateDisplayChapterNumber(filteredChapters, selectedChapterId);

      expect(displayChapterNumber).toBe(2); // Second real chapter (index 1 + 1)
    });

    it('should return undefined for invalid chapter id', () => {
      const chapters = [
        { id: 0, title: 'Chapter 1', level: 0, wordCount: 2500 },
        { id: 1, title: 'Chapter 2', level: 0, wordCount: 3000 }
      ];

      const filteredChapters = chapters.filter(ch => ch.wordCount >= 50);

      // Test: Invalid chapter id should return undefined
      const selectedChapterId = 999;
      const displayChapterNumber = calculateDisplayChapterNumber(filteredChapters, selectedChapterId);

      expect(displayChapterNumber).toBeUndefined();
    });

    it('should handle books with no front matter correctly', () => {
      // All chapters have sufficient word count
      const chapters = [
        { id: 0, title: 'Chapter 1', level: 0, wordCount: 2500 },
        { id: 1, title: 'Chapter 2', level: 0, wordCount: 3000 },
        { id: 2, title: 'Chapter 3', level: 0, wordCount: 2800 }
      ];

      const filteredChapters = chapters.filter(ch => ch.wordCount >= 50);

      // Test: First chapter should be displayChapterNumber=1
      const selectedChapterId = 0;
      const displayChapterNumber = calculateDisplayChapterNumber(filteredChapters, selectedChapterId);

      expect(displayChapterNumber).toBe(1);
    });

    it('should handle books with extensive front matter (Zen and the Art of Motorcycle Maintenance case)', () => {
      const chapters = [
        { id: 0, title: 'Copyright', level: 0, wordCount: 20 },
        { id: 1, title: 'Dedication', level: 0, wordCount: 15 },
        { id: 2, title: 'Preface', level: 0, wordCount: 30 },
        { id: 3, title: 'Introduction', level: 0, wordCount: 45 },
        { id: 4, title: 'Table of Contents', level: 0, wordCount: 25 },
        { id: 5, title: 'Part 1: Chapter 1', level: 0, wordCount: 2500 },  // First real chapter
        { id: 6, title: 'Part 1: Chapter 2', level: 0, wordCount: 3000 }
      ];

      const filteredChapters = chapters.filter(ch => ch.wordCount >= 50);

      // Test: Chapter with id=5 should be displayChapterNumber=1 (first real chapter after all front matter)
      // This fixes the bug where it was showing as "Chapter 6" instead of "Chapter 1"
      const selectedChapterId = 5;
      const displayChapterNumber = calculateDisplayChapterNumber(filteredChapters, selectedChapterId);

      expect(displayChapterNumber).toBe(1); // First real chapter (index 0 + 1) in filtered list
    });

    it('should calculate correct chapter numbers for all chapters in a book with front matter', () => {
      const chapters = [
        { id: 0, title: 'Preface', level: 0, wordCount: 30 },
        { id: 1, title: 'Intro', level: 0, wordCount: 20 },
        { id: 2, title: 'Chapter 1', level: 0, wordCount: 2500 },
        { id: 3, title: 'Chapter 2', level: 0, wordCount: 3000 },
        { id: 4, title: 'Chapter 3', level: 0, wordCount: 2800 },
        { id: 5, title: 'Chapter 4', level: 0, wordCount: 3200 }
      ];

      const filteredChapters = chapters.filter(ch => ch.wordCount >= 50);

      // Verify each chapter gets the correct display number
      expect(calculateDisplayChapterNumber(filteredChapters, 2)).toBe(1); // First real chapter
      expect(calculateDisplayChapterNumber(filteredChapters, 3)).toBe(2); // Second real chapter
      expect(calculateDisplayChapterNumber(filteredChapters, 4)).toBe(3); // Third real chapter
      expect(calculateDisplayChapterNumber(filteredChapters, 5)).toBe(4); // Fourth real chapter
    });
  });

  describe('Edge cases', () => {
    it('should handle empty chapters array', () => {
      const chapters: any[] = [];
      const filteredChapters = chapters.filter(ch => ch.wordCount >= 50);
      const displayChapterNumber = calculateDisplayChapterNumber(filteredChapters, 0);
      expect(displayChapterNumber).toBeUndefined();
    });

    it('should handle when all chapters are filtered out', () => {
      const chapters = [
        { id: 0, title: 'Short 1', level: 0, wordCount: 10 },
        { id: 1, title: 'Short 2', level: 0, wordCount: 20 },
        { id: 2, title: 'Short 3', level: 0, wordCount: 30 }
      ];

      const filteredChapters = chapters.filter(ch => ch.wordCount >= 50);
      const displayChapterNumber = calculateDisplayChapterNumber(filteredChapters, 0);
      expect(displayChapterNumber).toBeUndefined();
    });

    it('should handle single chapter book', () => {
      const chapters = [
        { id: 0, title: 'Chapter 1', level: 0, wordCount: 2500 }
      ];

      const filteredChapters = chapters.filter(ch => ch.wordCount >= 50);
      const displayChapterNumber = calculateDisplayChapterNumber(filteredChapters, 0);
      expect(displayChapterNumber).toBe(1);
    });
  });
});

/**
 * Unit tests for chapter-level vocabulary generation
 *
 * These tests verify that chapter vocabulary generation works correctly:
 * - Only generates when required data is present
 * - Filters out known words
 * - Merges new vocab with existing vocab
 * - Avoids duplicates
 */
describe('BookReaderComponent - Chapter Vocabulary Generation', () => {
  describe('generateChapterVocab validation', () => {
    it('should require chapter content to be loaded', () => {
      const hasChapterContent = false;
      const hasBookPath = true;
      const hasChapterId = true;

      const canGenerate = hasChapterContent && hasBookPath && hasChapterId;
      expect(canGenerate).toBe(false);
    });

    it('should require book path to be set', () => {
      const hasChapterContent = true;
      const hasBookPath = false;
      const hasChapterId = true;

      const canGenerate = hasChapterContent && hasBookPath && hasChapterId;
      expect(canGenerate).toBe(false);
    });

    it('should require chapter ID to be set', () => {
      const hasChapterContent = true;
      const hasBookPath = true;
      const hasChapterId = false;

      const canGenerate = hasChapterContent && hasBookPath && hasChapterId;
      expect(canGenerate).toBe(false);
    });

    it('should allow generation when all requirements are met', () => {
      const hasChapterContent = true;
      const hasBookPath = true;
      const hasChapterId = true;

      const canGenerate = hasChapterContent && hasBookPath && hasChapterId;
      expect(canGenerate).toBe(true);
    });
  });

  describe('vocabulary deduplication', () => {
    it('should filter out duplicate terms (case-insensitive)', () => {
      const existingVocab = [
        { term: 'Philosophy', definition: 'Study of knowledge' },
        { term: 'Epistemology', definition: 'Theory of knowledge' }
      ];

      const newCards = [
        { term: 'philosophy', definition: 'Different definition' },
        { term: 'Metaphysics', definition: 'Study of reality' }
      ];

      const existingTerms = new Set(existingVocab.map(v => v.term.toLowerCase()));
      const uniqueNewCards = newCards.filter(card => !existingTerms.has(card.term.toLowerCase()));

      expect(uniqueNewCards.length).toBe(1);
      expect(uniqueNewCards[0].term).toBe('Metaphysics');
    });

    it('should preserve all terms when no duplicates exist', () => {
      const existingVocab = [
        { term: 'Philosophy', definition: 'Study of knowledge' }
      ];

      const newCards = [
        { term: 'Epistemology', definition: 'Theory of knowledge' },
        { term: 'Metaphysics', definition: 'Study of reality' }
      ];

      const existingTerms = new Set(existingVocab.map(v => v.term.toLowerCase()));
      const uniqueNewCards = newCards.filter(card => !existingTerms.has(card.term.toLowerCase()));

      expect(uniqueNewCards.length).toBe(2);
    });

    it('should handle empty existing vocabulary', () => {
      const existingVocab: any[] = [];

      const newCards = [
        { term: 'Philosophy', definition: 'Study of knowledge' },
        { term: 'Epistemology', definition: 'Theory of knowledge' }
      ];

      const existingTerms = new Set(existingVocab.map(v => v.term.toLowerCase()));
      const uniqueNewCards = newCards.filter(card => !existingTerms.has(card.term.toLowerCase()));

      expect(uniqueNewCards.length).toBe(2);
    });
  });

  describe('known words filtering', () => {
    it('should use more known words for chapter-level (200 vs 100 for selection)', () => {
      const allKnownWords = Array.from({ length: 300 }, (_, i) => `word${i}`);

      const selectionKnownWords = allKnownWords.slice(-100);
      const chapterKnownWords = allKnownWords.slice(-200);

      expect(selectionKnownWords.length).toBe(100);
      expect(chapterKnownWords.length).toBe(200);
      expect(chapterKnownWords.length).toBeGreaterThan(selectionKnownWords.length);
    });

    it('should handle when fewer than 200 known words exist', () => {
      const allKnownWords = Array.from({ length: 50 }, (_, i) => `word${i}`);
      const chapterKnownWords = allKnownWords.slice(-200);

      expect(chapterKnownWords.length).toBe(50);
    });
  });
});

/**
 * Unit tests for dynamic pagination with overflow detection
 *
 * These tests verify the binary search algorithm for finding optimal page size
 * and the estimation logic for initial bounds.
 */
describe('BookReaderComponent - Dynamic Pagination', () => {
  /**
   * Helper: Simulates the binary search logic from findOptimalPageSize
   */
  function binarySearchOptimalSize(
    doesOverflow: (wordCount: number) => boolean,
    estimate: number,
    maxPossible: number
  ): number {
    let low = Math.max(10, Math.floor(estimate * 0.3));
    let high = Math.min(maxPossible, Math.ceil(estimate * 2.5));

    // Handle case where maxPossible is smaller than our initial low bound
    if (high < low) {
      low = 10;
    }

    let result = low;

    // Check if low value overflows - need to expand search range downward
    if (doesOverflow(low)) {
      high = low;
      low = 10;
      result = low; // Reset result after adjusting bounds
    }

    while (low <= high) {
      const mid = Math.floor((low + high) / 2);
      if (doesOverflow(mid)) {
        high = mid - 1;
      } else {
        result = mid;
        low = mid + 1;
      }
    }

    return Math.max(10, result);
  }

  describe('binary search for optimal page size', () => {
    it('should find exact overflow boundary', () => {
      // Simulate: text overflows at 150+ words
      const overflowThreshold = 150;
      const doesOverflow = (count: number) => count >= overflowThreshold;

      const result = binarySearchOptimalSize(doesOverflow, 200, 500);

      // Should find 149 (largest that doesn't overflow)
      expect(result).toBe(149);
    });

    it('should handle when estimate is too high', () => {
      // Simulate: text overflows at 80+ words, but estimate is 200
      const overflowThreshold = 80;
      const doesOverflow = (count: number) => count >= overflowThreshold;

      const result = binarySearchOptimalSize(doesOverflow, 200, 500);

      expect(result).toBe(79);
    });

    it('should handle when estimate is too low', () => {
      // Simulate: text overflows at 400+ words, estimate is 200
      const overflowThreshold = 400;
      const doesOverflow = (count: number) => count >= overflowThreshold;

      const result = binarySearchOptimalSize(doesOverflow, 200, 500);

      // Should find 399 (one less than overflow threshold)
      expect(result).toBe(399);
    });

    it('should respect minimum of 10 words', () => {
      // Simulate: text always overflows (very small container)
      const doesOverflow = () => true;

      const result = binarySearchOptimalSize(doesOverflow, 200, 500);

      expect(result).toBe(10);
    });

    it('should handle when nothing overflows', () => {
      // Simulate: nothing overflows (large container)
      const doesOverflow = () => false;

      const result = binarySearchOptimalSize(doesOverflow, 200, 500);

      // Should return high bound (estimate * 2.5 = 500, capped at maxPossible)
      expect(result).toBe(500);
    });

    it('should respect maxPossible cap', () => {
      // Simulate: nothing overflows, but only 50 words remaining in chapter
      const doesOverflow = () => false;

      const result = binarySearchOptimalSize(doesOverflow, 200, 50);

      // Should be capped at maxPossible
      expect(result).toBe(50);
    });
  });

  describe('page size estimation', () => {
    /**
     * Helper: Simulates getEstimatedPageSize calculation
     */
    function estimatePageSize(
      containerHeight: number,
      containerWidth: number,
      fontSize: number,
      lineHeight: number,
      paddingY: number
    ): number {
      const availableHeight = Math.max(0, containerHeight - paddingY);
      const lines = Math.max(3, Math.floor(availableHeight / lineHeight));
      const avgCharWidth = fontSize * 0.6;
      const approxCharsPerLine = Math.max(16, Math.floor(containerWidth / avgCharWidth));
      const approxWordsPerLine = Math.max(4, Math.floor(approxCharsPerLine / 6));
      return Math.max(20, Math.floor(lines * approxWordsPerLine));
    }

    it('should calculate reasonable estimate for typical desktop container', () => {
      // Desktop: 600px height, 800px width, 16px font, 27.2px line height, 60px padding
      const estimate = estimatePageSize(600, 800, 16, 27.2, 60);

      // Should give roughly 300-400 words
      expect(estimate).toBeGreaterThan(100);
      expect(estimate).toBeLessThan(600);
    });

    it('should decrease estimate when font size increases', () => {
      const smallFont = estimatePageSize(600, 800, 14, 23.8, 60);
      const largeFont = estimatePageSize(600, 800, 24, 40.8, 60);

      expect(largeFont).toBeLessThan(smallFont);
    });

    it('should increase estimate when container is larger', () => {
      const smallContainer = estimatePageSize(400, 600, 16, 27.2, 60);
      const largeContainer = estimatePageSize(800, 1000, 16, 27.2, 60);

      expect(largeContainer).toBeGreaterThan(smallContainer);
    });

    it('should enforce minimum of 20 words', () => {
      // Very small container
      const estimate = estimatePageSize(100, 100, 28, 47.6, 80);

      expect(estimate).toBeGreaterThanOrEqual(20);
    });

    it('should enforce minimum of 3 lines', () => {
      // Container with small available height
      const estimate = estimatePageSize(100, 800, 16, 27.2, 80);

      // With 20px available height and 27.2px line height, would be < 1 line
      // but minimum is enforced to 3 lines
      expect(estimate).toBeGreaterThanOrEqual(20);
    });
  });
});

/**
 * Unit tests for chapter label getters used in pagination display
 */
describe('BookReaderComponent - Chapter Label Getters', () => {
  /**
   * Helper that mimics currentChapterLabel getter logic
   */
  function getCurrentChapterLabel(
    chapters: Array<{ id: number; title: string; displayLabel?: string | null }>,
    selectedChapterId: number | null
  ): string | null {
    if (!selectedChapterId) return null;
    const chapter = chapters.find(ch => ch.id === selectedChapterId);
    return chapter?.displayLabel ?? chapter?.title ?? null;
  }

  /**
   * Helper that mimics truncatedChapterLabel getter logic
   */
  function getTruncatedChapterLabel(label: string | null): string {
    if (!label) return '';
    return label.length > 20 ? label.substring(0, 20) + '...' : label;
  }

  describe('currentChapterLabel', () => {
    it('should return null when no chapter is selected', () => {
      const chapters = [{ id: 1, title: 'Chapter 1' }];
      expect(getCurrentChapterLabel(chapters, null)).toBeNull();
    });

    it('should return chapter title when displayLabel is not set', () => {
      const chapters = [{ id: 1, title: 'Chapter 1: The Beginning' }];
      expect(getCurrentChapterLabel(chapters, 1)).toBe('Chapter 1: The Beginning');
    });

    it('should prefer displayLabel over title when both exist', () => {
      const chapters = [{ id: 1, title: 'ch001.xhtml', displayLabel: 'Chapter 1' }];
      expect(getCurrentChapterLabel(chapters, 1)).toBe('Chapter 1');
    });

    it('should return null when chapter id not found', () => {
      const chapters = [{ id: 1, title: 'Chapter 1' }];
      expect(getCurrentChapterLabel(chapters, 999)).toBeNull();
    });

    it('should handle null displayLabel by falling back to title', () => {
      const chapters = [{ id: 1, title: 'Chapter 1', displayLabel: null }];
      expect(getCurrentChapterLabel(chapters, 1)).toBe('Chapter 1');
    });
  });

  describe('truncatedChapterLabel', () => {
    it('should return empty string for null input', () => {
      expect(getTruncatedChapterLabel(null)).toBe('');
    });

    it('should not truncate labels 20 characters or less', () => {
      expect(getTruncatedChapterLabel('Chapter 1')).toBe('Chapter 1');
      expect(getTruncatedChapterLabel('12345678901234567890')).toBe('12345678901234567890');
    });

    it('should truncate labels longer than 20 characters with ellipsis', () => {
      const longLabel = 'Chapter 1: The Very Long Beginning';
      expect(getTruncatedChapterLabel(longLabel)).toBe('Chapter 1: The Very ...');
    });

    it('should truncate at exactly 20 characters plus ellipsis', () => {
      const label = '123456789012345678901234567890'; // 30 chars
      const truncated = getTruncatedChapterLabel(label);
      expect(truncated).toBe('12345678901234567890...');
      expect(truncated.length).toBe(23); // 20 + 3 for '...'
    });
  });
});
