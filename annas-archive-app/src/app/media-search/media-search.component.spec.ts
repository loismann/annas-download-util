import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MatDialog } from '@angular/material/dialog';
import { Observable, Subject, of, throwError } from 'rxjs';

import { MediaSearchComponent } from './media-search.component';
import {
  MediaLookupResult, MediaQueueResponse, MediaSearchApiService
} from '../services/media-search-api.service';
import { SeasonPickerModalData } from '../season-picker-modal/season-picker-modal.component';
import { MediaLibraryApiService } from '../services/media-library-api.service';
import { AiApiService } from '../services/ai-api.service';
import { AuthService } from '../services/auth.service';
import { LoggerService } from '../services/logger.service';

/**
 * Characterization tests for the TV/movie search-and-acquire page.
 *
 * A review pass in the same series as the library grids. It found the plain
 * search subscription unguarded while the page's other two reads were guarded —
 * see "leaving the page".
 *
 * The AI path gets its own section because it is the only place in this app
 * where one user action fans out into many lookups, and the concurrency cap on
 * that fan-out exists for a reason recorded in ASSERTIONS.
 */
describe('MediaSearchComponent (characterization)', () => {
  let fixture: ComponentFixture<MediaSearchComponent>;
  let component: MediaSearchComponent;
  let api: jasmine.SpyObj<MediaSearchApiService>;
  let libraryApi: jasmine.SpyObj<MediaLibraryApiService>;
  let aiApi: jasmine.SpyObj<AiApiService>;
  let dialog: jasmine.SpyObj<MatDialog>;

  // ─── Fixtures ────────────────────────────────────────────────────────

  function show(over: Partial<MediaLookupResult> = {}): MediaLookupResult {
    return {
      title: 'The Outer Limits', tvdbId: 200, id: 1,
      seasons: [{ seasonNumber: 1 }, { seasonNumber: 2 }],
      ...over
    };
  }

  function film(over: Partial<MediaLookupResult> = {}): MediaLookupResult {
    return { title: 'Them!', tmdbId: 100, id: 1, ...over };
  }

  function queue(over: Partial<MediaQueueResponse> = {}): MediaQueueResponse {
    return { tv: { records: [] }, movies: { records: [] }, ...over };
  }

  /** Runs the AI path and waits for its promise chain to settle. */
  async function settle(): Promise<void> {
    for (let i = 0; i < 100 && component.loading; i++) {
      await new Promise(resolve => setTimeout(resolve, 1));
    }
  }

  /**
   * Lets the library cross-reference land.
   *
   * It is a forkJoin over the side that is needed plus a `Promise.resolve([])`
   * standing in for the side that is not — and a promise resolves on the
   * microtask queue, so the whole join emits after the synchronous call
   * returns, however immediate the stubs are.
   */
  async function crossReferenced(): Promise<void> {
    await new Promise(resolve => setTimeout(resolve, 0));
  }

  beforeEach(async () => {
    api = jasmine.createSpyObj<MediaSearchApiService>('MediaSearchApiService', [
      'searchTv', 'searchMovies', 'getTvLibrary', 'addTvShow', 'addMovie',
      'updateTvSeasons', 'getQueue'
    ]);
    api.searchTv.and.returnValue(of([]));
    api.searchMovies.and.returnValue(of([]));
    api.getTvLibrary.and.returnValue(of([]));
    api.addTvShow.and.returnValue(of(show({ id: 55 })));
    api.addMovie.and.returnValue(of(film({ id: 66 })));
    api.updateTvSeasons.and.returnValue(of(show({ id: 77 })));
    api.getQueue.and.returnValue(of(queue()));

    libraryApi = jasmine.createSpyObj<MediaLibraryApiService>('MediaLibraryApiService', ['getDownloadedMovies']);
    libraryApi.getDownloadedMovies.and.returnValue(of([]));

    aiApi = jasmine.createSpyObj<AiApiService>('AiApiService', ['aiMediaSearch']);
    aiApi.aiMediaSearch.and.returnValue(of({ results: [] } as any));

    dialog = jasmine.createSpyObj<MatDialog>('MatDialog', ['open']);
    dialog.open.and.returnValue({ afterClosed: () => of(undefined) } as any);

    await TestBed.configureTestingModule({
      imports: [MediaSearchComponent, NoopAnimationsModule],
      providers: [
        { provide: MediaSearchApiService, useValue: api },
        { provide: MediaLibraryApiService, useValue: libraryApi },
        { provide: AiApiService, useValue: aiApi },
        { provide: AuthService, useValue: { isAdmin: () => true, getOwnerName: () => 'Paul' } },
        { provide: LoggerService, useValue: jasmine.createSpyObj('LoggerService', ['error', 'warn', 'info', 'log', 'debug']) }
      ]
    })
      .overrideProvider(MatDialog, { useValue: dialog })
      .compileComponents();

    fixture = TestBed.createComponent(MediaSearchComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => fixture.destroy());

  // ─── Searching ───────────────────────────────────────────────────────

  describe('searching', () => {
    it('should search Sonarr by default and Radarr on the toggle', () => {
      component.searchTerm = 'outer limits';
      component.onSearch();
      expect(api.searchTv).toHaveBeenCalledWith('outer limits');

      component.searchingMovies = true;
      component.onSearch();
      expect(api.searchMovies).toHaveBeenCalledWith('outer limits');
    });

    it('should not search on an empty box', () => {
      component.searchTerm = '   ';

      component.onSearch();

      expect(api.searchTv).not.toHaveBeenCalled();
    });

    it('should trim the term', () => {
      component.searchTerm = '  them!  ';

      component.onSearch();

      expect(api.searchTv).toHaveBeenCalledWith('them!');
    });

    it('should tag every result with the type that was searched', () => {
      // The card renders differently per type, and an AI search can return
      // both — so the type belongs on the entry, not on the page.
      api.searchMovies.and.returnValue(of([film()]));
      component.searchingMovies = true;
      component.searchTerm = 'them';

      component.onSearch();

      expect(component.entries.length).toBe(1);
      expect(component.entries[0].mediaType).toBe('movie');
      expect(component.entries[0].addState).toBe('idle');
    });

    it('should clear the previous results before searching again', () => {
      api.searchTv.and.returnValue(of([show()]));
      component.searchTerm = 'a';
      component.onSearch();

      api.searchTv.and.returnValue(new Subject<MediaLookupResult[]>().asObservable());
      component.onSearch();

      expect(component.entries).toEqual([]);
      expect(component.loading).toBe(true);
    });

    it('should name the likely culprit when the search fails', () => {
      api.searchTv.and.returnValue(throwError(() => new Error('ECONNREFUSED')));
      component.searchTerm = 'a';

      component.onSearch();

      expect(component.error).toContain('Sonarr/Radarr');
      expect(component.loading).toBe(false);
    });
  });

  // ─── Cross-referencing what is already there ─────────────────────────

  describe('cross-referencing the library', () => {
    it('should mark a movie already in Radarr as added', async () => {
      api.searchMovies.and.returnValue(of([film({ tmdbId: 100 })]));
      libraryApi.getDownloadedMovies.and.returnValue(of([film({ tmdbId: 100, id: 42, hasFile: true })]));
      component.searchingMovies = true;
      component.searchTerm = 'them';

      component.onSearch();
      await crossReferenced();

      expect(component.entries[0].addState).toBe('added');
      expect(component.entries[0].addedId).toBe(42);
      expect(component.entries[0].progressLabel).toContain('Already have');
    });

    it('should distinguish requested from downloaded for a movie', async () => {
      api.searchMovies.and.returnValue(of([film({ tmdbId: 100 })]));
      libraryApi.getDownloadedMovies.and.returnValue(of([film({ tmdbId: 100, id: 42, hasFile: false })]));
      component.searchingMovies = true;
      component.searchTerm = 'them';

      component.onSearch();
      await crossReferenced();

      expect(component.entries[0].progressLabel).toContain('downloading');
    });

    it('should tell monitored seasons apart from seasons with files', async () => {
      // Monitored means "asked for"; episodeFileCount means "actually here".
      // Conflating them would show a season as ready before it downloads.
      api.searchTv.and.returnValue(of([show({ tvdbId: 200 })]));
      api.getTvLibrary.and.returnValue(of([show({
        tvdbId: 200, id: 9,
        seasons: [
          { seasonNumber: 1, monitored: true, statistics: { episodeFileCount: 10 } },
          { seasonNumber: 2, monitored: true, statistics: { episodeFileCount: 0 } },
          { seasonNumber: 3, monitored: false, statistics: { episodeFileCount: 0 } }
        ]
      })]));
      component.searchTerm = 'outer';

      component.onSearch();
      await crossReferenced();

      expect(component.entries[0].existingSeriesId).toBe(9);
      expect(component.entries[0].alreadyAddedSeasons).toEqual([1, 2]);
      expect(component.entries[0].downloadedSeasons).toEqual([1]);
    });

    it('should leave a show that is not in Sonarr alone', async () => {
      api.searchTv.and.returnValue(of([show({ tvdbId: 999 })]));
      api.getTvLibrary.and.returnValue(of([show({ tvdbId: 200, id: 9 })]));
      component.searchTerm = 'outer';

      component.onSearch();
      await crossReferenced();

      expect(component.entries[0].existingSeriesId).toBeUndefined();
      expect(component.entries[0].addState).toBe('idle');
    });

    it('should only ask for the library it needs', () => {
      api.searchMovies.and.returnValue(of([film()]));
      component.searchingMovies = true;
      component.searchTerm = 'them';

      component.onSearch();

      expect(libraryApi.getDownloadedMovies).toHaveBeenCalled();
      expect(api.getTvLibrary).not.toHaveBeenCalled();
    });

    it('should still show results when the cross-reference fails', async () => {
      // Knowing what you already own is a nicety; the search results are the point.
      api.searchTv.and.returnValue(of([show()]));
      api.getTvLibrary.and.returnValue(throwError(() => new Error('down')));
      component.searchTerm = 'outer';

      component.onSearch();
      await crossReferenced();

      expect(component.entries.length).toBe(1);
      expect(component.error).toBeNull();
    });
  });

  // ─── Adding ──────────────────────────────────────────────────────────

  describe('adding', () => {
    it('should add a movie without asking about seasons', () => {
      api.searchMovies.and.returnValue(of([film()]));
      component.searchingMovies = true;
      component.searchTerm = 'them';
      component.onSearch();

      component.onAdd(component.entries[0]);

      expect(dialog.open).not.toHaveBeenCalled();
      expect(api.addMovie).toHaveBeenCalled();
      expect(component.entries[0].addState).toBe('added');
      expect(component.entries[0].addedId).toBe(66);
      expect(component.entries[0].progressLabel).toBe('Queued');
    });

    it('should ask which seasons before adding a show', () => {
      dialog.open.and.returnValue({ afterClosed: () => of([1, 2]) } as any);
      api.searchTv.and.returnValue(of([show()]));
      component.searchTerm = 'outer';
      component.onSearch();

      component.onAdd(component.entries[0]);

      expect(api.addTvShow).toHaveBeenCalledWith(jasmine.anything(), [1, 2]);
    });

    it('should do nothing when the season picker is cancelled', () => {
      // Cancelled is undefined; an empty array is a deliberate "none of them".
      dialog.open.and.returnValue({ afterClosed: () => of(undefined) } as any);
      api.searchTv.and.returnValue(of([show()]));
      component.searchTerm = 'outer';
      component.onSearch();

      component.onAdd(component.entries[0]);

      expect(api.addTvShow).not.toHaveBeenCalled();
      expect(component.entries[0].addState).toBe('idle');
    });

    it('should update seasons rather than re-add a show already in Sonarr', async () => {
      // Re-adding would ask Sonarr to create a series it already has.
      dialog.open.and.returnValue({ afterClosed: () => of([3]) } as any);
      api.searchTv.and.returnValue(of([show({ tvdbId: 200 })]));
      api.getTvLibrary.and.returnValue(of([show({ tvdbId: 200, id: 9, seasons: [{ seasonNumber: 1, monitored: true }] })]));
      component.searchTerm = 'outer';
      component.onSearch();
      await crossReferenced();

      component.onAdd(component.entries[0]);

      expect(api.updateTvSeasons).toHaveBeenCalledWith(9, [3]);
      expect(api.addTvShow).not.toHaveBeenCalled();
    });

    it('should show the season picker what is already monitored', async () => {
      api.searchTv.and.returnValue(of([show({ tvdbId: 200 })]));
      api.getTvLibrary.and.returnValue(of([show({
        tvdbId: 200, id: 9, seasons: [{ seasonNumber: 1, monitored: true }, { seasonNumber: 2 }]
      })]));
      component.searchTerm = 'outer';
      component.onSearch();
      await crossReferenced();

      component.onAdd(component.entries[0]);

      const passed = dialog.open.calls.mostRecent().args[1] as { data: SeasonPickerModalData };
      expect(passed.data.alreadyAddedSeasons).toEqual([1]);
    });

    it('should show the card as failed when the add is refused', () => {
      api.addMovie.and.returnValue(throwError(() => new Error('nope')));
      api.searchMovies.and.returnValue(of([film()]));
      component.searchingMovies = true;
      component.searchTerm = 'them';
      component.onSearch();

      component.onAdd(component.entries[0]);

      expect(component.entries[0].addState).toBe('error');
    });
  });

  // ─── Queue progress ──────────────────────────────────────────────────

  describe('queue progress', () => {
    /**
     * The poll interval is started in the *constructor*, not ngOnInit, so the
     * mock clock has to be in place before the component exists — installing it
     * inside a test would leave the interval bound to the real timer and every
     * tick() below would do nothing.
     */
    beforeEach(() => {
      fixture.destroy();
      jasmine.clock().install();
      fixture = TestBed.createComponent(MediaSearchComponent);
      component = fixture.componentInstance;
    });

    afterEach(() => jasmine.clock().uninstall());

    /** A page with one added movie, waiting on the queue. */
    function withAddedMovie(): void {
      api.searchMovies.and.returnValue(of([film()]));
      component.searchingMovies = true;
      component.searchTerm = 'them';
      component.onSearch();
      component.onAdd(component.entries[0]);
    }

    it('should not poll while nothing has been added', () => {
      api.searchMovies.and.returnValue(of([film()]));
      component.searchingMovies = true;
      component.searchTerm = 'them';
      component.onSearch();

      jasmine.clock().tick(10000);

      expect(api.getQueue).not.toHaveBeenCalled();
    });

    it('should report percent and time left while downloading', () => {
      withAddedMovie();
      api.getQueue.and.returnValue(of(queue({
        movies: { records: [{ movieId: 66, size: 1000, sizeleft: 250, timeleft: '00:05:00' }] }
      })));

      jasmine.clock().tick(10000);

      expect(component.entries[0].progressLabel).toBe('75% · 00:05:00');
    });

    it('should fall back to the queue status when there is no size yet', () => {
      withAddedMovie();
      api.getQueue.and.returnValue(of(queue({
        movies: { records: [{ movieId: 66, status: 'delay' }] }
      })));

      jasmine.clock().tick(10000);

      expect(component.entries[0].progressLabel).toBe('delay');
    });

    it('should treat leaving the queue as imported', () => {
      withAddedMovie();
      api.getQueue.and.returnValue(of(queue()));

      jasmine.clock().tick(10000);

      expect(component.entries[0].progressLabel).toContain('Imported');
    });

    it('should not match a movie against a TV queue record with the same id', () => {
      // Sonarr and Radarr number independently, so the same id can mean two
      // different things — the record list has to be picked by type first.
      withAddedMovie();
      api.getQueue.and.returnValue(of(queue({
        tv: { records: [{ seriesId: 66, size: 1000, sizeleft: 0 }] }
      })));

      jasmine.clock().tick(10000);

      expect(component.entries[0].progressLabel).toContain('Imported');
    });

    it('should stop polling after destroy', () => {
      withAddedMovie();
      const before = api.getQueue.calls.count();
      fixture.destroy();

      jasmine.clock().tick(30000);

      expect(api.getQueue.calls.count()).toBe(before);
    });
  });

  // ─── AI search ───────────────────────────────────────────────────────

  describe('AI search', () => {
    it('should carry the typed term into the AI box when opened', () => {
      component.searchTerm = '  cold war thrillers ';

      component.toggleAiSearch();

      expect(component.aiSearchExpanded).toBe(true);
      expect(component.aiSearchQuery).toBe('cold war thrillers');
    });

    it('should clear the AI box when closed', () => {
      component.toggleAiSearch();
      component.aiSearchQuery = 'something';

      component.toggleAiSearch();

      expect(component.aiSearchExpanded).toBe(false);
      expect(component.aiSearchQuery).toBe('');
    });

    it('should send plain Enter and let Shift+Enter make a newline', () => {
      component.aiSearchExpanded = true;
      component.aiSearchQuery = 'q';
      const plain = new KeyboardEvent('keydown', { key: 'Enter' });
      const prevented = spyOn(plain, 'preventDefault');

      component.onAiTextareaEnter(plain);
      expect(prevented).toHaveBeenCalled();
      expect(aiApi.aiMediaSearch).toHaveBeenCalledTimes(1);

      component.onAiTextareaEnter(new KeyboardEvent('keydown', { key: 'Enter', shiftKey: true }));
      expect(aiApi.aiMediaSearch).toHaveBeenCalledTimes(1);
    });

    it('should resolve each suggested title through the ordinary lookup', async () => {
      // The AI proposes titles; Sonarr/Radarr decide what they actually are.
      aiApi.aiMediaSearch.and.returnValue(of({
        results: [
          { title: 'Them!', year: 1954, type: 'movie' },
          { title: 'The Outer Limits', type: 'tv' }
        ]
      } as any));
      api.searchMovies.and.returnValue(of([film()]));
      api.searchTv.and.returnValue(of([show()]));
      component.aiSearchExpanded = true;
      component.aiSearchQuery = 'atomic monsters';

      component.onSearch();
      await settle();

      expect(api.searchMovies).toHaveBeenCalledWith('Them! (1954)');
      expect(api.searchTv).toHaveBeenCalledWith('The Outer Limits');
      expect(component.entries.map(e => e.mediaType)).toEqual(['movie', 'tv']);
    });

    it('should keep the AI order rather than whichever lookup returned first', async () => {
      // The suggestions are ranked; resolving them concurrently must not
      // reshuffle them into completion order.
      const slow = new Subject<MediaLookupResult[]>();
      aiApi.aiMediaSearch.and.returnValue(of({
        results: [
          { title: 'First', type: 'movie' },
          { title: 'Second', type: 'movie' }
        ]
      } as any));
      api.searchMovies.and.callFake((term: string) =>
        term === 'First' ? slow.asObservable() : of([film({ title: 'Second' })]));
      component.aiSearchExpanded = true;
      component.aiSearchQuery = 'q';

      component.onSearch();
      await new Promise(resolve => setTimeout(resolve, 5));
      slow.next([film({ title: 'First' })]);
      slow.complete();
      await settle();

      expect(component.entries.map(e => e.result.title)).toEqual(['First', 'Second']);
    });

    it('should drop a suggestion that resolves to nothing', async () => {
      aiApi.aiMediaSearch.and.returnValue(of({
        results: [{ title: 'Real', type: 'movie' }, { title: 'Imaginary', type: 'movie' }]
      } as any));
      api.searchMovies.and.callFake((term: string) =>
        term === 'Real' ? of([film({ title: 'Real' })]) : of([]));
      component.aiSearchExpanded = true;
      component.aiSearchQuery = 'q';

      component.onSearch();
      await settle();

      expect(component.entries.map(e => e.result.title)).toEqual(['Real']);
    });

    it('should survive one lookup failing among many', async () => {
      aiApi.aiMediaSearch.and.returnValue(of({
        results: [{ title: 'Good', type: 'movie' }, { title: 'Bad', type: 'movie' }]
      } as any));
      api.searchMovies.and.callFake((term: string) =>
        term === 'Good' ? of([film({ title: 'Good' })]) : throwError(() => new Error('boom')));
      component.aiSearchExpanded = true;
      component.aiSearchQuery = 'q';

      component.onSearch();
      await settle();

      expect(component.entries.map(e => e.result.title)).toEqual(['Good']);
    });

    it('should say so when nothing at all could be resolved', async () => {
      aiApi.aiMediaSearch.and.returnValue(of({
        results: [{ title: 'Imaginary', type: 'movie' }]
      } as any));
      api.searchMovies.and.returnValue(of([]));
      component.aiSearchExpanded = true;
      component.aiSearchQuery = 'q';

      component.onSearch();
      await settle();

      expect(component.entries).toEqual([]);
      expect(component.error).toContain('rephrasing');
    });

    it('should surface the backend\'s own message when the AI call fails', () => {
      aiApi.aiMediaSearch.and.returnValue(throwError(() => ({ error: { error: 'No API key configured' } })));
      component.aiSearchExpanded = true;
      component.aiSearchQuery = 'q';

      component.onSearch();

      expect(component.error).toBe('No API key configured');
      expect(component.loading).toBe(false);
    });

    it('should not send an empty AI query', () => {
      component.aiSearchExpanded = true;
      component.aiSearchQuery = '   ';

      component.onSearch();

      expect(aiApi.aiMediaSearch).not.toHaveBeenCalled();
    });
  });

  // ─── Leaving the page ────────────────────────────────────────────────

  describe('leaving the page', () => {
    /**
     * The defect this pass found.
     *
     * The page's other two reads — the queue poll and the library
     * cross-reference — were guarded. The search itself was not, and it is the
     * one that matters most: its response handler calls crossReferenceLibrary,
     * so a search landing after destroy did not merely set state on a dead
     * component, it started two more requests from it.
     */
    it('should not fetch the library from a search that lands after destroy', () => {
      const late = new Subject<MediaLookupResult[]>();
      api.searchTv.and.returnValue(late.asObservable());
      component.searchTerm = 'outer';
      component.onSearch();

      fixture.destroy();
      late.next([show()]);

      expect(component.entries).toEqual([]);
      expect(api.getTvLibrary).not.toHaveBeenCalled();
    });

    it('should let an add already in flight finish after destroy', () => {
      // Adds are POSTs. Unsubscribing would abort the request, so navigating
      // away would cancel the thing the user just asked Radarr for.
      let aborted = false;
      api.searchMovies.and.returnValue(of([film()]));
      component.searchingMovies = true;
      component.searchTerm = 'them';
      component.onSearch();
      api.addMovie.and.returnValue(new Observable<MediaLookupResult>(() => () => { aborted = true; }));
      component.onAdd(component.entries[0]);

      fixture.destroy();

      expect(aborted).toBe(false);
    });
  });

  // ─── Bulk import ─────────────────────────────────────────────────────

  describe('bulk import', () => {
    it('should open the bulk import dialog', () => {
      component.openBulkImport();

      expect(dialog.open).toHaveBeenCalled();
    });
  });
});
