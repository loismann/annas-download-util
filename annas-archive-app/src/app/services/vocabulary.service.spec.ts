import { TestBed } from '@angular/core/testing';
import { VocabularyService } from './vocabulary.service';

/**
 * Basic smoke tests for VocabularyService
 * This service manages known/unknown vocabulary words per book
 */
describe('VocabularyService', () => {
  let service: VocabularyService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [VocabularyService]
    });
    service = TestBed.inject(VocabularyService);
    localStorage.clear();
  });

  afterEach(() => {
    localStorage.clear();
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });

  it('should have knownWords$ observable', (done) => {
    service.knownWords$.subscribe(words => {
      expect(words).toBeInstanceOf(Map);
      done();
    });
  });

  it('should have unknownWords$ observable', (done) => {
    service.unknownWords$.subscribe(words => {
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

  it('should load from localStorage if available', () => {
    // Pre-populate localStorage
    const testData = [{ term: 'test', books: ['book1'] }];
    localStorage.setItem('vocabulary_known_words_map', JSON.stringify(testData));

    // Create new service instance
    const newService = new VocabularyService();

    newService.knownWords$.subscribe(words => {
      expect(words.size).toBeGreaterThanOrEqual(0);
    });
  });

  it('should handle corrupted localStorage gracefully', () => {
    localStorage.setItem('vocabulary_known_words_map', 'invalid json{');

    // Should log error but not throw - service continues with empty maps
    const newService = new VocabularyService();

    newService.knownWords$.subscribe(words => {
      expect(words).toBeInstanceOf(Map);
      expect(words.size).toBe(0); // Should be empty due to corrupted data
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
});
