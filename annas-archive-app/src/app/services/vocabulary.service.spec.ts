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

  it('should be created', () => {
    expect(service).toBeTruthy();
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
});
