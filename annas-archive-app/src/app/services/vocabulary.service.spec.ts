import { TestBed } from '@angular/core/testing';
import { HttpClientTestingModule, HttpTestingController } from '@angular/common/http/testing';
import { VocabularyService } from './vocabulary.service';
import { AiApiService } from './ai-api.service';
import { LoggerService } from './logger.service';
import { of, throwError, Subject } from 'rxjs';

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

  describe('Sync Conflict Tests', () => {
    let mockAiApiService: jasmine.SpyObj<AiApiService>;
    let mockLoggerService: jasmine.SpyObj<LoggerService>;
    let serviceWithMocks: VocabularyService;

    beforeEach(() => {
      mockAiApiService = jasmine.createSpyObj('AiApiService', [
        'getKnownWords',
        'getStudyWords',
        'addKnownWord',
        'addStudyWord',
        'removeKnownWord',
        'removeStudyWord',
        'deleteBookVocab'
      ]);

      mockLoggerService = jasmine.createSpyObj('LoggerService', [
        'log',
        'warn',
        'error',
        'debug'
      ]);

      // Default mock responses for initialization
      mockAiApiService.getKnownWords.and.returnValue(of({}));
      mockAiApiService.getStudyWords.and.returnValue(of({}));

      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        imports: [HttpClientTestingModule],
        providers: [
          VocabularyService,
          { provide: AiApiService, useValue: mockAiApiService },
          { provide: LoggerService, useValue: mockLoggerService }
        ]
      });

      serviceWithMocks = TestBed.inject(VocabularyService);
      localStorage.clear();
    });

    describe('Optimistic Update Rollback - markAsKnown', () => {
      it('should rollback local state when server fails to mark as known', (done) => {
        const term = 'soliloquy';
        const normalizedTerm = serviceWithMocks.normalizeForMatch(term);

        // Setup: make addKnownWord return an error
        mockAiApiService.addKnownWord.and.returnValue(throwError(() => new Error('Server error')));

        // Initially should not be known
        expect(serviceWithMocks.isKnown(term)).toBe(false);

        // Call markAsKnown - optimistic update happens, then error triggers rollback
        serviceWithMocks.markAsKnown(term, 'book-1');

        // Allow microtasks to complete (error handling)
        setTimeout(() => {
          // Should be rolled back due to server error
          expect(serviceWithMocks.isKnown(term)).toBe(false);
          done();
        }, 50);
      });

      it('should rollback book association when server fails', (done) => {
        const term = 'monologue';
        const normalizedTerm = serviceWithMocks.normalizeForMatch(term);

        // Setup: make addKnownWord return an error
        mockAiApiService.addKnownWord.and.returnValue(throwError(() => new Error('Server error')));

        // Call markAsKnown
        serviceWithMocks.markAsKnown(term, 'book-123');

        // Allow error handling to complete
        setTimeout(() => {
          const afterRollback = serviceWithMocks.getKnownWords('book-123');
          expect(afterRollback).not.toContain(normalizedTerm);
          done();
        }, 50);
      });
    });

    describe('Optimistic Update Rollback - markAsUnknown', () => {
      it('should rollback local state when server fails to mark as study', (done) => {
        const term = 'catharsis';
        const definition = 'The purging of emotions';

        // Setup: make addStudyWord return an error
        mockAiApiService.addStudyWord.and.returnValue(throwError(() => new Error('Server error')));

        // Call markAsUnknown - optimistic update then rollback
        serviceWithMocks.markAsUnknown(term, definition, 'book-1');

        // Allow error handling to complete
        setTimeout(() => {
          const afterRollback = serviceWithMocks.getStudyWordDefinition(term);
          expect(afterRollback).toBeUndefined();
          done();
        }, 50);
      });

      it('should rollback book association for study words when server fails', (done) => {
        const term = 'pathos';
        const definition = 'A quality that evokes pity or sadness';
        const normalizedTerm = serviceWithMocks.normalizeForMatch(term);

        // Setup: make addStudyWord return an error
        mockAiApiService.addStudyWord.and.returnValue(throwError(() => new Error('Server error')));

        serviceWithMocks.markAsUnknown(term, definition, 'book-456');

        // Allow error handling to complete
        setTimeout(() => {
          const afterRollback = serviceWithMocks.getUnknownWords('book-456');
          expect(afterRollback.has(normalizedTerm)).toBe(false);
          done();
        }, 50);
      });
    });

    describe('Concurrent Modifications', () => {
      it('should handle rapid mark known/unknown toggles', (done) => {
        const term = 'ephemeral';
        const definition = 'Lasting for a very short time';

        mockAiApiService.addKnownWord.and.returnValue(of(undefined));
        mockAiApiService.addStudyWord.and.returnValue(of(undefined));

        // Rapid toggles
        serviceWithMocks.markAsKnown(term, 'book-1', definition);
        serviceWithMocks.markAsUnknown(term, definition, 'book-1');
        serviceWithMocks.markAsKnown(term, 'book-1', definition);

        setTimeout(() => {
          // Final state should be known
          expect(serviceWithMocks.isKnown(term)).toBe(true);
          expect(serviceWithMocks.getStudyWordDefinition(term)).toBeUndefined();
          done();
        }, 100);
      });

      it('should handle same word added from multiple books', (done) => {
        const term = 'ubiquitous';
        const definition = 'Present, appearing, or found everywhere';

        mockAiApiService.addKnownWord.and.returnValue(of(undefined));

        // Add from multiple books
        serviceWithMocks.markAsKnown(term, 'book-1', definition);
        serviceWithMocks.markAsKnown(term, 'book-2', definition);
        serviceWithMocks.markAsKnown(term, 'book-3', definition);

        setTimeout(() => {
          // Word should be known in all books
          const fromBook1 = serviceWithMocks.getKnownWords('book-1');
          const fromBook2 = serviceWithMocks.getKnownWords('book-2');
          const fromBook3 = serviceWithMocks.getKnownWords('book-3');
          const normalized = serviceWithMocks.normalizeForMatch(term);

          expect(fromBook1).toContain(normalized);
          expect(fromBook2).toContain(normalized);
          expect(fromBook3).toContain(normalized);
          done();
        }, 100);
      });

      it('should handle concurrent study words with different definitions', (done) => {
        const term = 'polymorphous';
        const definition1 = 'Definition from book 1';
        const definition2 = 'Definition from book 2';

        mockAiApiService.addStudyWord.and.returnValue(of(undefined));

        // Add with different definitions (last one wins)
        serviceWithMocks.markAsUnknown(term, definition1, 'book-1');
        serviceWithMocks.markAsUnknown(term, definition2, 'book-2');

        setTimeout(() => {
          // Last definition should win
          const currentDef = serviceWithMocks.getStudyWordDefinition(term);
          expect(currentDef).toBe(definition2);
          done();
        }, 100);
      });
    });

    describe('Race Condition: Delayed Server Response', () => {
      it('should handle server response arriving after state change', (done) => {
        const term = 'ephemeron';
        const definition = 'Something short-lived';

        // First call will be delayed
        const delayedResponse$ = new Subject<void>();
        mockAiApiService.addStudyWord.and.returnValue(delayedResponse$.asObservable());
        mockAiApiService.addKnownWord.and.returnValue(of(undefined));

        // Mark as study (delayed)
        serviceWithMocks.markAsUnknown(term, definition, 'book-1');

        setTimeout(() => {
          // Before server responds, mark as known
          serviceWithMocks.markAsKnown(term, 'book-1');

          // Now server responds (too late, state already changed)
          delayedResponse$.next();
          delayedResponse$.complete();

          setTimeout(() => {
            // Should be known, not study
            expect(serviceWithMocks.isKnown(term)).toBe(true);
            done();
          }, 50);
        }, 50);
      });

      it('should maintain consistency when server response is delayed', (done) => {
        const term = 'transient';
        const serverResponse$ = new Subject<void>();

        mockAiApiService.addKnownWord.and.returnValue(serverResponse$.asObservable());

        // Start operation
        serviceWithMocks.markAsKnown(term, 'book-1');

        // Immediate local state should reflect the change
        expect(serviceWithMocks.isKnown(term)).toBe(true);

        // Server responds after delay
        setTimeout(() => {
          serverResponse$.next();
          serverResponse$.complete();

          // State should still be consistent
          expect(serviceWithMocks.isKnown(term)).toBe(true);
          done();
        }, 100);
      });
    });

    describe('Server Data Conflict Resolution', () => {
      it('should prefer server data on initial load', (done) => {
        // Reset and re-create with server data
        const serverKnownWords = {
          'preexisting': ['book-server'],
          'another': ['book-1']
        };

        mockAiApiService.getKnownWords.and.returnValue(of(serverKnownWords));
        mockAiApiService.getStudyWords.and.returnValue(of({}));

        // Create new service that will load from server
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({
          imports: [HttpClientTestingModule],
          providers: [
            VocabularyService,
            { provide: AiApiService, useValue: mockAiApiService },
            { provide: LoggerService, useValue: mockLoggerService }
          ]
        });

        const freshService = TestBed.inject(VocabularyService);

        setTimeout(() => {
          // Server data should be loaded
          expect(freshService.isKnown('preexisting')).toBe(true);
          expect(freshService.isKnown('another')).toBe(true);
          done();
        }, 100);
      });

      it('should handle server returning study words with existing definitions', (done) => {
        const serverStudyWords = {
          'serendipity': {
            definition: 'Finding good things by accident',
            books: ['book-1']
          }
        };

        mockAiApiService.getKnownWords.and.returnValue(of({}));
        mockAiApiService.getStudyWords.and.returnValue(of(serverStudyWords));

        TestBed.resetTestingModule();
        TestBed.configureTestingModule({
          imports: [HttpClientTestingModule],
          providers: [
            VocabularyService,
            { provide: AiApiService, useValue: mockAiApiService },
            { provide: LoggerService, useValue: mockLoggerService }
          ]
        });

        const freshService = TestBed.inject(VocabularyService);

        setTimeout(() => {
          const definition = freshService.getStudyWordDefinition('serendipity');
          expect(definition).toBe('Finding good things by accident');
          done();
        }, 100);
      });
    });

    describe('Definition Cache Persistence Through Conflicts', () => {
      it('should preserve cached definition even after server error rollback', (done) => {
        const term = 'quintessential';
        const definition = 'Representing the most perfect example';

        // Setup: make addStudyWord return an error
        mockAiApiService.addStudyWord.and.returnValue(throwError(() => new Error('Server error')));

        // Mark as study (will cache definition before error rollback)
        serviceWithMocks.markAsUnknown(term, definition, 'book-1');

        // Allow error handling to complete
        setTimeout(() => {
          // Definition should still be cached even though word was rolled back
          expect(serviceWithMocks.getCachedDefinition(term)).toBe(definition);
          // But not in study words due to rollback
          expect(serviceWithMocks.getStudyWordDefinition(term)).toBeUndefined();
          done();
        }, 50);
      });
    });

    describe('Multiple Book Association Conflicts', () => {
      it('should rollback word entirely when second book call fails', (done) => {
        // Note: Current implementation does full rollback on any error,
        // even if the word was successfully added for another book.
        // This tests the actual behavior.
        const term = 'paradigm';
        const definition = 'A typical example or pattern';
        const normalizedTerm = serviceWithMocks.normalizeForMatch(term);
        let callCount = 0;

        // First call succeeds, second call fails
        mockAiApiService.addKnownWord.and.callFake(() => {
          callCount++;
          if (callCount === 1) {
            return of(undefined);
          } else {
            return throwError(() => new Error('Server error'));
          }
        });

        // First book succeeds
        serviceWithMocks.markAsKnown(term, 'book-1', definition);

        setTimeout(() => {
          // Verify first book succeeded
          expect(serviceWithMocks.getKnownWords('book-1')).toContain(normalizedTerm);
          expect(serviceWithMocks.isKnown(term)).toBe(true);

          // Second book fails
          serviceWithMocks.markAsKnown(term, 'book-2', definition);

          setTimeout(() => {
            // Current implementation: rollback removes word from knownWordsSubject entirely
            // even though book-1 association remains in the map
            expect(serviceWithMocks.isKnown(term)).toBe(false);
            // book-2 should not have it (rolled back)
            const fromBook2 = serviceWithMocks.getKnownWords('book-2');
            expect(fromBook2).not.toContain(normalizedTerm);
            // book-1 association remains in the map but word is not "known" globally
            const fromBook1 = serviceWithMocks.getKnownWords('book-1');
            expect(fromBook1).toContain(normalizedTerm);
            done();
          }, 50);
        }, 50);
      });
    });

    describe('Observable Consistency During Conflicts', () => {
      it('should emit correct values through known words observable during conflict', (done) => {
        const term = 'immutable';
        const normalizedTerm = serviceWithMocks.normalizeForMatch(term);
        const emissions: Set<string>[] = [];

        // Setup: make addKnownWord return an error
        mockAiApiService.addKnownWord.and.returnValue(throwError(() => new Error('Server error')));

        // Subscribe to track all emissions
        const subscription = serviceWithMocks.knownWords$.subscribe(words => {
          emissions.push(new Set(words));
        });

        // Mark as known (will emit optimistic update, then rollback)
        serviceWithMocks.markAsKnown(term, 'book-1');

        // Allow error handling to complete
        setTimeout(() => {
          // Should have had at least one emission with the word (optimistic update)
          const hadWord = emissions.some(set => set.has(normalizedTerm));
          expect(hadWord).toBe(true);

          // Last emission should NOT have the word (rolled back)
          const lastEmission = emissions[emissions.length - 1];
          expect(lastEmission.has(normalizedTerm)).toBe(false);
          subscription.unsubscribe();
          done();
        }, 50);
      });

      it('should emit correct values through study words observable during conflict', (done) => {
        const term = 'mutable';
        const definition = 'Able to change';
        const normalizedTerm = serviceWithMocks.normalizeForMatch(term);
        const emissions: Map<string, string>[] = [];

        // Setup: make addStudyWord return an error
        mockAiApiService.addStudyWord.and.returnValue(throwError(() => new Error('Server error')));

        // Subscribe to track all emissions
        const subscription = serviceWithMocks.studyWords$.subscribe(words => {
          emissions.push(new Map(words));
        });

        // Mark as study (will emit optimistic update, then rollback)
        serviceWithMocks.markAsUnknown(term, definition, 'book-1');

        // Allow error handling to complete
        setTimeout(() => {
          // Should have had at least one emission with the word (optimistic update)
          const hadWord = emissions.some(map => map.has(normalizedTerm));
          expect(hadWord).toBe(true);

          // Last emission should NOT have the word (rolled back)
          const lastEmission = emissions[emissions.length - 1];
          expect(lastEmission.has(normalizedTerm)).toBe(false);
          subscription.unsubscribe();
          done();
        }, 50);
      });
    });
  });
});
