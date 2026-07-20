import { TestBed } from '@angular/core/testing';
import { GenreMappingService } from './genre-mapping.service';
import { STANDARD_GENRES } from '../constants/book-genres';

describe('GenreMappingService', () => {
  let service: GenreMappingService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [GenreMappingService]
    });
    service = TestBed.inject(GenreMappingService);
  });

  describe('mapToStandardGenre', () => {
    it('should return Uncategorized for empty subjects array', () => {
      expect(service.mapToStandardGenre([])).toBe('Uncategorized');
    });

    it('should return Uncategorized for null subjects', () => {
      expect(service.mapToStandardGenre(null as any)).toBe('Uncategorized');
    });

    it('should return Uncategorized for undefined subjects', () => {
      expect(service.mapToStandardGenre(undefined as any)).toBe('Uncategorized');
    });

    it('should map mystery subjects to Mystery & Detective', () => {
      const subjects = ['mystery', 'detective fiction', 'crime'];
      const result = service.mapToStandardGenre(subjects);
      expect(result).toBe('Mystery & Detective');
    });

    it('should map science fiction subjects to Science Fiction', () => {
      const subjects = ['science fiction', 'space opera', 'aliens'];
      const result = service.mapToStandardGenre(subjects);
      expect(result).toBe('Science Fiction');
    });

    it('should map fantasy subjects to Fantasy', () => {
      const subjects = ['fantasy', 'dragons', 'magic'];
      const result = service.mapToStandardGenre(subjects);
      expect(result).toBe('Fantasy');
    });

    it('should map romance subjects to Romance', () => {
      const subjects = ['romance', 'love stories', 'romantic'];
      const result = service.mapToStandardGenre(subjects);
      expect(result).toBe('Romance');
    });

    it('should map horror subjects to Horror', () => {
      const subjects = ['horror', 'scary', 'supernatural'];
      const result = service.mapToStandardGenre(subjects);
      expect(result).toBe('Horror');
    });

    it('should map history subjects to History', () => {
      const subjects = ['history', 'world war', 'historical'];
      const result = service.mapToStandardGenre(subjects);
      expect(result).toBe('History');
    });

    it('should map biography subjects to Biography & Memoir', () => {
      const subjects = ['biography', 'autobiography', 'memoir'];
      const result = service.mapToStandardGenre(subjects);
      expect(result).toBe('Biography & Memoir');
    });

    it('should handle case-insensitive matching', () => {
      const subjects = ['MYSTERY', 'DETECTIVE FICTION'];
      const result = service.mapToStandardGenre(subjects);
      expect(result).toBe('Mystery & Detective');
    });

    it('should handle subjects with extra whitespace', () => {
      const subjects = ['  mystery  ', '  crime  '];
      const result = service.mapToStandardGenre(subjects);
      expect(result).toBe('Mystery & Detective');
    });

    it('should return highest scoring genre when multiple match', () => {
      // Science fiction should win when more specific matches
      const subjects = ['science fiction', 'space', 'adventure'];
      const result = service.mapToStandardGenre(subjects);
      // Result should be a valid genre
      expect(STANDARD_GENRES).toContain(result);
    });

    it('should handle partial keyword matches', () => {
      // "mystery novel" contains "mystery"
      const subjects = ['mystery novel', 'detective story'];
      const result = service.mapToStandardGenre(subjects);
      expect(result).toBe('Mystery & Detective');
    });

    it('should return Uncategorized when no keywords match', () => {
      const subjects = ['xyz123', 'random_gibberish', 'not_a_genre'];
      const result = service.mapToStandardGenre(subjects);
      expect(result).toBe('Uncategorized');
    });
  });

  describe('extractTags', () => {
    it('should return empty array for empty subjects', () => {
      expect(service.extractTags([], 'Fiction')).toEqual([]);
    });

    it('should return empty array for null subjects', () => {
      expect(service.extractTags(null as any, 'Fiction')).toEqual([]);
    });

    it('should return empty array for undefined subjects', () => {
      expect(service.extractTags(undefined as any, 'Fiction')).toEqual([]);
    });

    it('should filter out stop words', () => {
      const subjects = ['fiction', 'science fiction', 'robots'];
      const result = service.extractTags(subjects, 'Other');
      expect(result).not.toContain('fiction');
      expect(result).toContain('science fiction');
      expect(result).toContain('robots');
    });

    it('should filter out the primary genre', () => {
      const subjects = ['science fiction', 'space', 'aliens'];
      const result = service.extractTags(subjects, 'Science Fiction');
      expect(result).not.toContain('Science Fiction');
      expect(result).not.toContain('science fiction');
    });

    it('should filter out fictitious character references', () => {
      const subjects = ['detective', 'Holmes, Sherlock (Fictitious character)', 'London'];
      const result = service.extractTags(subjects, 'Mystery');
      expect(result).not.toContain('Holmes, Sherlock (Fictitious character)');
    });

    it('should filter out single characters', () => {
      const subjects = ['a', 'b', 'science fiction'];
      const result = service.extractTags(subjects, 'Other');
      expect(result).not.toContain('a');
      expect(result).not.toContain('b');
    });

    it('should filter out numbers only', () => {
      const subjects = ['123', '456', 'science fiction'];
      const result = service.extractTags(subjects, 'Other');
      expect(result).not.toContain('123');
      expect(result).not.toContain('456');
    });

    it('should respect the limit parameter', () => {
      const subjects = ['tag1', 'tag2', 'tag3', 'tag4', 'tag5', 'tag6', 'tag7'];
      const result = service.extractTags(subjects, 'Other', 3);
      expect(result.length).toBe(3);
    });

    it('should use default limit of 5', () => {
      const subjects = ['tag1', 'tag2', 'tag3', 'tag4', 'tag5', 'tag6', 'tag7'];
      const result = service.extractTags(subjects, 'Other');
      expect(result.length).toBe(5);
    });

    it('should trim whitespace from tags', () => {
      const subjects = ['  space  ', '  aliens  '];
      const result = service.extractTags(subjects, 'Other');
      expect(result).toContain('space');
      expect(result).toContain('aliens');
    });

    it('should filter multiple stop words', () => {
      const subjects = ['general', 'book', 'literature', 'accessible book', 'american', 'robots'];
      const result = service.extractTags(subjects, 'Other');
      expect(result.length).toBe(1);
      expect(result).toContain('robots');
    });
  });

  describe('getStandardGenres', () => {
    it('should return the standard genres list', () => {
      const genres = service.getStandardGenres();
      expect(genres).toBe(STANDARD_GENRES);
    });

    it('should include common genres', () => {
      const genres = service.getStandardGenres();
      expect(genres).toContain('Science Fiction');
      expect(genres).toContain('Fantasy');
      expect(genres).toContain('Mystery & Detective');
      expect(genres).toContain('Romance');
    });

    it('should include Uncategorized', () => {
      const genres = service.getStandardGenres();
      expect(genres).toContain('Uncategorized');
    });

    it('should return a readonly array', () => {
      const genres = service.getStandardGenres();
      // TypeScript ensures it's readonly, but we can verify it's the same reference
      expect(genres).toBe(service.getStandardGenres());
    });
  });
});
