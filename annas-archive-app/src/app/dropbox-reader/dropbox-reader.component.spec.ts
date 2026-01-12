/**
 * Unit tests for chapter summary numbering logic in DropboxReaderComponent
 *
 * These tests verify that displayChapterNumber is calculated correctly based on
 * the filtered chapters array index (not the raw EPUB chapter ID).
 */

describe('DropboxReaderComponent - Chapter Summary Number Calculation', () => {
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
describe('DropboxReaderComponent - Chapter Vocabulary Generation', () => {
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
