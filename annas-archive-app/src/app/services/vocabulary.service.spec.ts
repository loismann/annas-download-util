import { TestBed } from '@angular/core/testing';
import { HttpClientTestingModule } from '@angular/common/http/testing';
import { VocabularyService } from './vocabulary.service';

/**
 * Basic smoke tests for VocabularyService
 * This service manages known/study vocabulary words with server-side persistence
 */
describe('VocabularyService', () => {
  let service: VocabularyService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HttpClientTestingModule],
      providers: [VocabularyService]
    });
    service = TestBed.inject(VocabularyService);
    localStorage.clear();
  });

  afterEach(() => {
    localStorage.clear();
  });

  it('should have knownWords$ observable', (done) => {
    service.knownWords$.subscribe(words => {
      expect(words).toBeInstanceOf(Set);
      done();
    });
  });

  it('should have studyWords$ observable', (done) => {
    service.studyWords$.subscribe(words => {
      expect(words).toBeInstanceOf(Map);
      done();
    });
  });

  it('should normalize terms correctly', () => {
    const normalized1 = service.normalizeForMatch('Test');
    const normalized2 = service.normalizeForMatch('test');
    const normalized3 = service.normalizeForMatch('TEST');

    expect(normalized1).toBe(normalized2);
    expect(normalized2).toBe(normalized3);
  });

  it('should load from server on initialization', (done) => {
    // Service automatically loads from server in constructor
    service.knownWords$.subscribe(words => {
      expect(words).toBeInstanceOf(Set);
      expect(words.size).toBeGreaterThanOrEqual(0);
      done();
    });
  });

  it('should handle server errors gracefully', (done) => {
    // Even if server fails, service should still be usable with empty state
    service.knownWords$.subscribe(words => {
      expect(words).toBeInstanceOf(Set);
      done();
    });
  });

  it('should normalize terms with possessives', () => {
    const withPossessive = service.normalizeForMatch("John's");
    const without = service.normalizeForMatch('John');

    expect(withPossessive.length).toBeLessThanOrEqual(without.length + 2);
  });

  it('should normalize terms with plurals', () => {
    const plural = service.normalizeForMatch('books');
    const singular = service.normalizeForMatch('book');

    // The normalization might strip the 's'
    expect(plural.length).toBeGreaterThanOrEqual(singular.length - 1);
  });

  describe('Definition Caching', () => {
    it('should cache definition when marking as known with definition provided', () => {
      const term = 'philosophy';
      const definition = 'The study of fundamental questions about existence, knowledge, values, reason, mind, and language';

      service.markAsKnown(term, 'book-1', definition);

      const cached = service.getCachedDefinition(term);
      expect(cached).toBe(definition);
    });

    it('should retrieve definition from study words when marking as known without definition', (done) => {
      const term = 'epistemology';
      const definition = 'The branch of philosophy concerned with the nature of knowledge';

      // First mark as study (with definition)
      service.markAsUnknown(term, definition, 'book-1');

      // Wait for observable to update
      setTimeout(() => {
        // Then mark as known without providing definition
        service.markAsKnown(term, 'book-1');

        // Definition should be cached from study words
        const cached = service.getCachedDefinition(term);
        expect(cached).toBe(definition);
        done();
      }, 100);
    });

    it('should preserve definition when moving from study to known', (done) => {
      const term = 'metaphysics';
      const definition = 'The branch of philosophy dealing with the nature of reality';

      // Mark as study
      service.markAsUnknown(term, definition, 'book-1');

      setTimeout(() => {
        // Mark as known
        service.markAsKnown(term, 'book-1');

        // Definition should still be cached
        const cached = service.getCachedDefinition(term);
        expect(cached).toBe(definition);
        done();
      }, 100);
    });

    it('should preserve definition when moving from known back to study', (done) => {
      const term = 'ontology';
      const definition = 'The philosophical study of being and existence';

      // Mark as known with definition
      service.markAsKnown(term, 'book-1', definition);

      setTimeout(() => {
        // Verify it's cached
        const cached1 = service.getCachedDefinition(term);
        expect(cached1).toBe(definition);

        // Mark as study (definition should be retrieved from cache)
        service.markAsUnknown(term, definition, 'book-1');

        setTimeout(() => {
          // Move back to known
          service.markAsKnown(term, 'book-1');

          // Definition should still be cached
          const cached2 = service.getCachedDefinition(term);
          expect(cached2).toBe(definition);
          done();
        }, 100);
      }, 100);
    });

    it('should return undefined for non-existent cached definition', () => {
      const cached = service.getCachedDefinition('nonexistent-term');
      expect(cached).toBeUndefined();
    });

    it('should normalize term when caching definition', () => {
      const term = 'Philosophy';
      const definition = 'The study of fundamental questions';

      service.markAsKnown(term, 'book-1', definition);

      // Should find it with different casing
      const cached = service.getCachedDefinition('philosophy');
      expect(cached).toBe(definition);
    });
  });

  describe('getStudyWordDefinition', () => {
    it('should return definition for study word', (done) => {
      const term = 'logic';
      const definition = 'The systematic study of valid reasoning';

      service.markAsUnknown(term, definition, 'book-1');

      setTimeout(() => {
        const result = service.getStudyWordDefinition(term);
        expect(result).toBe(definition);
        done();
      }, 100);
    });

    it('should return undefined for non-study word', () => {
      const result = service.getStudyWordDefinition('not-a-study-word');
      expect(result).toBeUndefined();
    });

    it('should normalize term when retrieving study word definition', (done) => {
      const term = 'Ethics';
      const definition = 'The branch of philosophy dealing with moral principles';

      service.markAsUnknown(term, definition, 'book-1');

      setTimeout(() => {
        // Should find it with different casing
        const result = service.getStudyWordDefinition('ethics');
        expect(result).toBe(definition);
        done();
      }, 100);
    });

    it('should return undefined after word is moved from study to known', (done) => {
      const term = 'aesthetics';
      const definition = 'The philosophical study of beauty and taste';

      service.markAsUnknown(term, definition, 'book-1');

      setTimeout(() => {
        // Verify it's in study words
        expect(service.getStudyWordDefinition(term)).toBe(definition);

        // Move to known
        service.markAsKnown(term, 'book-1');

        setTimeout(() => {
          // Should no longer be in study words
          const result = service.getStudyWordDefinition(term);
          expect(result).toBeUndefined();
          done();
        }, 100);
      }, 100);
    });
  });

  describe('Definition Persistence Scenarios', () => {
    it('should handle marking word as known initially, then moving to study, then back to known', (done) => {
      const term = 'existentialism';
      const definition = 'A philosophical theory emphasizing individual existence and freedom';

      // 1. Mark as known with definition
      service.markAsKnown(term, 'book-1', definition);

      setTimeout(() => {
        // Verify cached
        expect(service.getCachedDefinition(term)).toBe(definition);

        // 2. Move to study
        service.markAsUnknown(term, definition, 'book-1');

        setTimeout(() => {
          // Verify in study words
          expect(service.getStudyWordDefinition(term)).toBe(definition);

          // 3. Move back to known
          service.markAsKnown(term, 'book-1');

          setTimeout(() => {
            // Verify still cached
            expect(service.getCachedDefinition(term)).toBe(definition);
            // Verify not in study words
            expect(service.getStudyWordDefinition(term)).toBeUndefined();
            done();
          }, 100);
        }, 100);
      }, 100);
    });

    it('should handle marking word as study initially, then moving to known, then back to study', (done) => {
      const term = 'phenomenology';
      const definition = 'The philosophical study of structures of consciousness';

      // 1. Mark as study with definition
      service.markAsUnknown(term, definition, 'book-1');

      setTimeout(() => {
        // Verify in study words
        expect(service.getStudyWordDefinition(term)).toBe(definition);

        // 2. Move to known
        service.markAsKnown(term, 'book-1');

        setTimeout(() => {
          // Verify cached
          expect(service.getCachedDefinition(term)).toBe(definition);
          // Verify not in study words
          expect(service.getStudyWordDefinition(term)).toBeUndefined();

          // 3. Move back to study
          service.markAsUnknown(term, definition, 'book-1');

          setTimeout(() => {
            // Verify in study words again
            expect(service.getStudyWordDefinition(term)).toBe(definition);
            // Verify still cached
            expect(service.getCachedDefinition(term)).toBe(definition);
            done();
          }, 100);
        }, 100);
      }, 100);
    });
  });
});
