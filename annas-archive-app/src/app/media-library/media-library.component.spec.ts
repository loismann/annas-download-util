import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MatDialog } from '@angular/material/dialog';
import { Router } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';

import { MediaLibraryComponent } from './media-library.component';
import { MediaLibraryApiService } from '../services/media-library-api.service';
import {
  MediaLookupResult, MediaQueueItem, MediaQueueResponse, MediaSearchApiService
} from '../services/media-search-api.service';
import { AuthService } from '../services/auth.service';
import { LoggerService } from '../services/logger.service';

/**
 * Characterization tests for the TV/movie library.
 *
 * A review pass in the same series as the audiobook grid, the Date Night pool
 * and the Date Night lobby. What it found is in "download progress": the
 * per-item percent is aggregated across every queue record for that item, but
 * the ETA beside it was not.
 *
 * The pure view helpers already have their own suite in media-library-view.spec
 * — this covers what the component itself does: loading, the queue-progress
 * arithmetic, optimistic favorites, bulk edits and the destructive dialogs.
 */
describe('MediaLibraryComponent (characterization)', () => {
  let fixture: ComponentFixture<MediaLibraryComponent>;
  let component: MediaLibraryComponent;
  let api: jasmine.SpyObj<MediaLibraryApiService>;
  let searchApi: jasmine.SpyObj<MediaSearchApiService>;
  let dialog: jasmine.SpyObj<MatDialog>;
  let router: jasmine.SpyObj<Router>;
  let ownerName: 'Paul' | 'Mom' | 'Dad' | null;

  // ─── Fixtures ────────────────────────────────────────────────────────

  function movie(over: Partial<MediaLookupResult> = {}): MediaLookupResult {
    return { id: 1, tmdbId: 100, title: 'Them!', year: 1954, hasFile: true, ...over };
  }

  function series(over: Partial<MediaLookupResult> = {}): MediaLookupResult {
    return { id: 1, tvdbId: 200, title: 'The Outer Limits', ...over };
  }

  function queue(over: Partial<MediaQueueResponse> = {}): MediaQueueResponse {
    return { tv: { records: [] }, movies: { records: [] }, ...over };
  }

  function record(over: Partial<MediaQueueItem> = {}): MediaQueueItem {
    return { size: 1000, sizeleft: 500, ...over };
  }

  /** A closed dialog that hands `result` back to afterClosed(). */
  function closesWith(result: unknown): void {
    dialog.open.and.returnValue({ afterClosed: () => of(result) } as any);
  }

  beforeEach(async () => {
    ownerName = 'Paul';

    api = jasmine.createSpyObj<MediaLibraryApiService>('MediaLibraryApiService', [
      'getDownloadedMovies', 'getDownloadedTv', 'setMovieFavorite', 'setTvFavorite',
      'setMovieMetadata', 'setTvMetadata', 'deleteMovie', 'deleteSeries', 'watchMovie',
      'getMovieStreamUrl', 'getMovieHlsMasterUrl', 'getMovieSubtitleUrl',
      'getMovieDownloadUrl', 'saveMovieProgress', 'searchMovieReleases', 'grabMovieRelease'
    ]);
    api.getDownloadedMovies.and.returnValue(of([]));
    api.getDownloadedTv.and.returnValue(of([]));
    api.setMovieFavorite.and.returnValue(of({ favorites: ['Paul'] } as any));
    api.setTvFavorite.and.returnValue(of({ favorites: ['Paul'] } as any));
    api.setMovieMetadata.and.returnValue(of({} as any));
    api.setTvMetadata.and.returnValue(of({} as any));
    api.deleteMovie.and.returnValue(of({} as any));
    api.deleteSeries.and.returnValue(of({} as any));
    api.watchMovie.and.returnValue(of({ mode: 'embed', embedUrl: 'http://x/embed' } as any));

    searchApi = jasmine.createSpyObj<MediaSearchApiService>('MediaSearchApiService', ['getQueue']);
    searchApi.getQueue.and.returnValue(of(queue()));

    dialog = jasmine.createSpyObj<MatDialog>('MatDialog', ['open']);
    closesWith(undefined);

    router = jasmine.createSpyObj<Router>('Router', ['navigate']);

    await TestBed.configureTestingModule({
      imports: [MediaLibraryComponent, NoopAnimationsModule],
      providers: [
        { provide: MediaLibraryApiService, useValue: api },
        { provide: MediaSearchApiService, useValue: searchApi },
        { provide: Router, useValue: router },
        { provide: AuthService, useValue: { getOwnerName: () => ownerName } },
        { provide: LoggerService, useValue: jasmine.createSpyObj('LoggerService', ['error', 'warn', 'info', 'log', 'debug']) }
      ]
    })
      // Not a plain provider: the component imports MatDialogModule, whose own
      // MatDialog provider lands in the same injector and wins. Overriding is
      // what actually replaces it — without this the real dialog service opens
      // real overlays and the failures point at Material's internals.
      .overrideProvider(MatDialog, { useValue: dialog })
      .compileComponents();

    fixture = TestBed.createComponent(MediaLibraryComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => fixture.destroy());

  // ─── Loading ─────────────────────────────────────────────────────────

  describe('loading', () => {
    it('should open on movies', () => {
      component.ngOnInit();

      expect(component.showingMovies).toBe(true);
      expect(api.getDownloadedMovies).toHaveBeenCalled();
      expect(api.getDownloadedTv).not.toHaveBeenCalled();
    });

    it('should filter to the signed-in person by default', () => {
      // Mom sees Mom's, Dad sees Dad's — the same default the ebook library
      // uses. Still an ordinary filter afterwards.
      ownerName = 'Mom';

      component.ngOnInit();

      expect([...component.selectedOwners]).toEqual(['Mom']);
    });

    it('should not preselect an owner for a session that has no name', () => {
      ownerName = null;

      component.ngOnInit();

      expect(component.selectedOwners.size).toBe(0);
    });

    it('should count a season as downloaded only when it has episode files', () => {
      api.getDownloadedTv.and.returnValue(of([series({
        seasons: [
          { seasonNumber: 0, statistics: { episodeFileCount: 3 } },
          { seasonNumber: 1, statistics: { episodeFileCount: 5 } },
          { seasonNumber: 2, statistics: { episodeFileCount: 0 } }
        ] as any
      })]));
      component.showingMovies = false;

      component.ngOnInit();

      // Season 0 is specials: it counts as downloaded but never toward the
      // total, so a complete show does not read "2 of 3 seasons".
      expect(component.tvTiles[0].downloadedSeasonCount).toBe(2);
      expect(component.tvTiles[0].totalSeasonCount).toBe(2);
    });

    it('should name the likely culprit when the library will not load', () => {
      api.getDownloadedMovies.and.returnValue(throwError(() => new Error('ECONNREFUSED')));

      component.ngOnInit();

      expect(component.error).toContain('Sonarr/Radarr');
      expect(component.loading).toBe(false);
    });

    it('should clear a stale error when a load succeeds', () => {
      component.ngOnInit();
      component.error = 'something went wrong earlier';

      component.toggleShowing();

      expect(component.error).toBeNull();
    });

    it('should drop a bulk selection when switching between TV and movies', () => {
      // Sonarr and Radarr number their records independently, so a seriesId and
      // a movieId can be the same number — carrying a selection across the tabs
      // would apply an edit to whatever happened to share the ID.
      component.ngOnInit();
      component.bulkEditMode = true;
      component.toggleTileSelection(7);
      expect(component.selectedForBulk.has(7)).toBe(true);

      component.showingMovies = false;
      component.toggleShowing();

      expect(component.selectedForBulk.size).toBe(0);
    });

    it('should stop polling the queue after destroy', () => {
      component.ngOnInit();
      const callsAtDestroy = searchApi.getQueue.calls.count();

      fixture.destroy();

      expect(searchApi.getQueue.calls.count()).toBe(callsAtDestroy);
    });

    it('should not apply a load that lands after destroy', () => {
      const late = new Subject<MediaLookupResult[]>();
      api.getDownloadedMovies.and.returnValue(late.asObservable());
      component.ngOnInit();

      fixture.destroy();
      late.next([movie()]);

      expect(component.movieTiles).toEqual([]);
    });
  });

  // ─── Download progress ───────────────────────────────────────────────

  describe('download progress', () => {
    it('should sum every record for one series', () => {
      // A season downloads as several episodes at once, so one series' progress
      // is the total across all of its queue records rather than one of them.
      searchApi.getQueue.and.returnValue(of(queue({
        tv: { records: [
          record({ seriesId: 5, size: 1000, sizeleft: 500 }),
          record({ seriesId: 5, size: 1000, sizeleft: 0 })
        ] }
      })));

      component.ngOnInit();

      expect(component.getProgress(5)?.percent).toBe(75);
    });

    it('should keep TV and movie records apart', () => {
      searchApi.getQueue.and.returnValue(of(queue({
        tv: { records: [record({ seriesId: 5, size: 100, sizeleft: 100 })] },
        movies: { records: [record({ movieId: 9, size: 100, sizeleft: 0 })] }
      })));

      component.ngOnInit();

      expect(component.getProgress(5)?.percent).toBe(0);
      expect(component.getProgress(9)?.percent).toBe(100);
    });

    it('should ignore records with no item to attach to', () => {
      searchApi.getQueue.and.returnValue(of(queue({
        tv: { records: [record({ seriesId: undefined })] }
      })));

      component.ngOnInit();

      expect(component.getProgress(undefined)).toBeNull();
    });

    it('should skip an item whose size is not known yet', () => {
      // Dividing by it would be NaN%, and Sonarr reports zero briefly on grab.
      searchApi.getQueue.and.returnValue(of(queue({
        tv: { records: [record({ seriesId: 5, size: 0, sizeleft: 0 })] }
      })));

      component.ngOnInit();

      expect(component.getProgress(5)).toBeNull();
    });

    /**
     * The defect this pass found.
     *
     * Percent is aggregated across every record for the item; the ETA next to
     * it took `records[0]` — whichever episode Sonarr happened to list first.
     * A show with one episode a minute out and another forty minutes out
     * therefore showed a part-way progress bar labelled "one minute", and then
     * sat there. The ETA now comes from the record with the most left, which is
     * the one the show is actually waiting on.
     */
    it('should quote the ETA of the record with the most left to fetch', () => {
      searchApi.getQueue.and.returnValue(of(queue({
        tv: { records: [
          record({ seriesId: 5, size: 1000, sizeleft: 50, timeleft: '00:01:00' }),
          record({ seriesId: 5, size: 1000, sizeleft: 900, timeleft: '00:40:00' })
        ] }
      })));

      component.ngOnInit();

      expect(component.getProgress(5)?.etaLabel).toBe('00:40:00');
    });

    it('should not guess a speed from a single reading', () => {
      // Speed is not a field the queue API returns; it is the change in
      // sizeleft between two polls, so the first poll has nothing to compare.
      searchApi.getQueue.and.returnValue(of(queue({
        movies: { records: [record({ movieId: 9, size: 1000, sizeleft: 800 })] }
      })));

      component.ngOnInit();

      expect(component.getProgress(9)?.speedLabel).toBeNull();
    });

    it('should derive a speed once a second reading lands', () => {
      jasmine.clock().install();
      // Speed is bytes-over-seconds, so the clock has to move the wall clock
      // too — with only the timer mocked both readings carry the same
      // timestamp and the elapsed time is zero.
      jasmine.clock().mockDate();
      try {
        searchApi.getQueue.and.returnValue(of(queue({
          movies: { records: [record({ movieId: 9, size: 10_000_000, sizeleft: 8_000_000 })] }
        })));
        component.ngOnInit();

        searchApi.getQueue.and.returnValue(of(queue({
          movies: { records: [record({ movieId: 9, size: 10_000_000, sizeleft: 6_000_000 })] }
        })));
        jasmine.clock().tick(10000);

        // 2 MB recovered over 10 s.
        expect(component.getProgress(9)?.speedLabel).toContain('/s');
      } finally {
        jasmine.clock().uninstall();
      }
    });

    it('should drop an item that has left the queue', () => {
      // The map is rebuilt each poll rather than merged, so a finished download
      // stops showing a stuck progress bar.
      jasmine.clock().install();
      try {
        searchApi.getQueue.and.returnValue(of(queue({
          movies: { records: [record({ movieId: 9 })] }
        })));
        component.ngOnInit();
        expect(component.getProgress(9)).not.toBeNull();

        searchApi.getQueue.and.returnValue(of(queue()));
        jasmine.clock().tick(10000);

        expect(component.getProgress(9)).toBeNull();
      } finally {
        jasmine.clock().uninstall();
      }
    });

    it('should keep polling after a failed poll', () => {
      // A blip must not silently end progress reporting for the session.
      jasmine.clock().install();
      try {
        searchApi.getQueue.and.returnValue(throwError(() => new Error('down')));
        component.ngOnInit();
        const afterFirst = searchApi.getQueue.calls.count();

        jasmine.clock().tick(10000);

        expect(searchApi.getQueue.calls.count()).toBe(afterFirst + 1);
        expect(component.error).toBeNull();
      } finally {
        jasmine.clock().uninstall();
      }
    });
  });

  // ─── Filtering ───────────────────────────────────────────────────────

  describe('filtering', () => {
    beforeEach(() => {
      api.getDownloadedMovies.and.returnValue(of([
        movie({ id: 1, title: 'Them!', year: 1954, customGenres: ['Sci-Fi'], owners: ['Mom'] }),
        movie({ id: 2, title: 'The Blob', year: 1958, customGenres: ['Horror'], owners: ['Dad'] }),
        movie({ id: 3, title: 'Forbidden Planet', year: 1956, customGenres: ['Sci-Fi'], owners: ['Mom', 'Dad'] })
      ]));
      ownerName = null;
      component.ngOnInit();
    });

    it('should search by title', () => {
      component.searchTerm = 'blob';

      expect(component.filteredMovieTiles.map(m => m.id)).toEqual([2]);
    });

    it('should filter by genre', () => {
      component.selectedGenre = 'Sci-Fi';

      expect(component.filteredMovieTiles.map(m => m.id)).toEqual([1, 3]);
    });

    it('should treat several owners as "any of"', () => {
      component.toggleOwnerFilter('Dad');

      expect(component.filteredMovieTiles.map(m => m.id)).toEqual([2, 3]);
    });

    it('should list every genre once, sorted', () => {
      expect(component.genres).toEqual(['Horror', 'Sci-Fi']);
    });

    it('should report counts before and after filtering', () => {
      component.searchTerm = 'them';

      expect(component.totalCount).toBe(3);
      expect(component.filteredCount).toBe(1);
    });

    it('should sort by title and by year', () => {
      component.sortOrder = 'title';
      expect(component.filteredMovieTiles.map(m => m.title))
        .toEqual(['Forbidden Planet', 'The Blob', 'Them!']);

      component.sortOrder = 'year';
      expect(component.filteredMovieTiles.map(m => m.year)).toEqual([1958, 1956, 1954]);
    });

    it('should put everything back with one action', () => {
      component.searchTerm = 'them';
      component.selectedGenre = 'Sci-Fi';
      component.toggleOwnerFilter('Mom');
      component.filterFavoritesOnly = true;
      component.bulkEditMode = true;

      component.resetView();

      expect(component.searchTerm).toBe('');
      expect(component.selectedGenre).toBe('');
      expect(component.selectedOwners.size).toBe(0);
      expect(component.filterFavoritesOnly).toBe(false);
      expect(component.sortOrder).toBe('recent');
      expect(component.bulkEditMode).toBe(false);
      expect(component.filteredCount).toBe(3);
    });
  });

  // ─── Favorites ───────────────────────────────────────────────────────

  describe('favorites', () => {
    it('should show the star filled straight away and take the server\'s answer after', () => {
      const m = movie({ favorites: [] });
      api.setMovieFavorite.and.returnValue(of({ favorites: ['Paul', 'Mom'] } as any));
      component.ngOnInit();

      component.toggleFavorite(m, new Event('click'));

      expect(api.setMovieFavorite).toHaveBeenCalledWith(1, true);
      expect(m.favorites).toEqual(['Paul', 'Mom']);
    });

    it('should put the star back when the save fails', () => {
      const m = movie({ favorites: [] });
      api.setMovieFavorite.and.returnValue(throwError(() => new Error('nope')));
      component.ngOnInit();

      component.toggleFavorite(m, new Event('click'));

      expect(m.favorites).toEqual([]);
    });

    it('should un-favorite through the same path', () => {
      const m = movie({ favorites: ['Paul'] });
      api.setMovieFavorite.and.returnValue(of({ favorites: [] } as any));
      component.ngOnInit();

      expect(component.isFavorited(m)).toBe(true);
      component.toggleFavorite(m, new Event('click'));

      expect(api.setMovieFavorite).toHaveBeenCalledWith(1, false);
    });

    it('should route a show to the TV endpoint', () => {
      const s = series({ favorites: [] });
      component.showingMovies = false;
      component.ngOnInit();

      component.toggleFavorite(s, new Event('click'));

      expect(api.setTvFavorite).toHaveBeenCalled();
      expect(api.setMovieFavorite).not.toHaveBeenCalled();
    });

    it('should do nothing for a session with no name to favorite as', () => {
      ownerName = null;
      component.ngOnInit();

      component.toggleFavorite(movie(), new Event('click'));

      expect(api.setMovieFavorite).not.toHaveBeenCalled();
    });

    it('should not also open the item', () => {
      // The star sits on a tile whose whole surface plays the movie.
      const event = new Event('click');
      const stop = spyOn(event, 'stopPropagation');
      component.ngOnInit();

      component.toggleFavorite(movie(), event);

      expect(stop).toHaveBeenCalled();
    });
  });

  // ─── Bulk edit ───────────────────────────────────────────────────────

  describe('bulk edit', () => {
    beforeEach(() => {
      api.getDownloadedMovies.and.returnValue(of([
        movie({ id: 1, title: 'Them!', owners: ['Mom'], customGenres: ['Sci-Fi'] }),
        movie({ id: 2, title: 'The Blob', owners: [], customGenres: [] })
      ]));
      // No signed-in name, so nothing is pre-filtered by owner and these tests
      // are about the bulk selection rather than about who owns what.
      ownerName = null;
      component.ngOnInit();
      component.bulkEditMode = true;
    });

    it('should ignore tile clicks outside bulk mode', () => {
      component.bulkEditMode = false;

      component.toggleTileSelection(1);

      expect(component.selectedForBulk.size).toBe(0);
    });

    it('should select and deselect', () => {
      component.toggleTileSelection(1);
      expect(component.selectedForBulk.has(1)).toBe(true);

      component.toggleTileSelection(1);
      expect(component.selectedForBulk.has(1)).toBe(false);
    });

    it('should select what is visible, not what exists', () => {
      // "Select all" after filtering must not quietly include the rows the
      // filter is hiding.
      component.searchTerm = 'them';

      component.selectAllVisible();

      expect([...component.selectedForBulk]).toEqual([1]);
    });

    it('should not open the dialog with nothing selected', () => {
      component.openBulkEditDialog();

      expect(dialog.open).not.toHaveBeenCalled();
    });

    it('should apply the edit to every selected movie and leave bulk mode', () => {
      closesWith({ owners: ['Dad'], genres: ['Horror'], mode: 'replace' });
      component.toggleTileSelection(1);
      component.toggleTileSelection(2);

      component.openBulkEditDialog();

      expect(api.setMovieMetadata).toHaveBeenCalledTimes(2);
      expect(component.bulkEditMode).toBe(false);
      expect(component.selectedForBulk.size).toBe(0);
    });

    it('should replace what is there when told to replace', () => {
      closesWith({ owners: ['Dad'], genres: ['Horror'], mode: 'replace' });
      component.toggleTileSelection(1);

      component.openBulkEditDialog();

      expect(api.setMovieMetadata).toHaveBeenCalledWith(1, ['Dad'], ['Horror']);
    });

    it('should add to what is there when told to append', () => {
      // The selected items may already differ from each other, which is why the
      // dialog starts blank and offers the choice rather than assuming.
      closesWith({ owners: ['Dad'], genres: ['Horror'], mode: 'append' });
      component.toggleTileSelection(1);

      component.openBulkEditDialog();

      expect(api.setMovieMetadata).toHaveBeenCalledWith(1, ['Mom', 'Dad'], ['Sci-Fi', 'Horror']);
    });

    it('should leave a field alone when nothing was entered for it', () => {
      // An empty genre list means "do not touch genres", not "clear them".
      closesWith({ owners: ['Dad'], genres: [], mode: 'replace' });
      component.toggleTileSelection(1);

      component.openBulkEditDialog();

      expect(api.setMovieMetadata).toHaveBeenCalledWith(1, ['Dad'], ['Sci-Fi']);
    });

    it('should change nothing when the dialog is cancelled', () => {
      closesWith(undefined);
      component.toggleTileSelection(1);

      component.openBulkEditDialog();

      expect(api.setMovieMetadata).not.toHaveBeenCalled();
    });

    it('should name the items it could not save', () => {
      closesWith({ owners: ['Dad'], genres: [], mode: 'replace' });
      api.setMovieMetadata.and.returnValue(throwError(() => new Error('nope')));
      component.toggleTileSelection(1);
      component.toggleTileSelection(2);

      component.openBulkEditDialog();

      expect(component.error).toContain('Them!');
      expect(component.error).toContain('The Blob');
    });
  });

  // ─── Playing and deleting ────────────────────────────────────────────

  describe('playing a movie', () => {
    beforeEach(() => component.ngOnInit());

    it('should open the player once Jellyfin resolves it', () => {
      component.playMovie(movie());

      expect(api.watchMovie).toHaveBeenCalledWith(100);
      expect(dialog.open).toHaveBeenCalled();
      expect(component.resolvingMovieId).toBeNull();
    });

    it('should say so rather than fail when the file is not down yet', () => {
      component.playMovie(movie({ hasFile: false }));

      expect(api.watchMovie).not.toHaveBeenCalled();
      expect(component.error).toContain('finished downloading');
    });

    it('should not start a second lookup while one is resolving', () => {
      api.watchMovie.and.returnValue(new Subject<never>().asObservable());

      component.playMovie(movie({ id: 1, tmdbId: 100 }));
      component.playMovie(movie({ id: 2, tmdbId: 200 }));

      expect(api.watchMovie).toHaveBeenCalledTimes(1);
      expect(component.resolvingMovieId).toBe(100);
    });

    it('should blame the scan, not the user, when Jellyfin has no match', () => {
      api.watchMovie.and.returnValue(throwError(() => new Error('404')));

      component.playMovie(movie());

      expect(component.error).toContain('Jellyfin');
      // And the card has to become clickable again, or the page needs a reload.
      expect(component.resolvingMovieId).toBeNull();
    });

    it('should not open a player over a page the user has left', () => {
      const late = new Subject<any>();
      api.watchMovie.and.returnValue(late.asObservable());
      component.playMovie(movie());

      fixture.destroy();
      late.next({ mode: 'embed', embedUrl: 'http://x' });

      expect(dialog.open).not.toHaveBeenCalled();
    });
  });

  describe('deleting', () => {
    beforeEach(() => component.ngOnInit());

    it('should ask before deleting a movie', () => {
      closesWith(false);
      api.getDownloadedMovies.and.returnValue(of([movie()]));
      component.ngOnInit();

      component.deleteMovie(component.movieTiles[0], new Event('click'));

      expect(dialog.open).toHaveBeenCalled();
      expect(api.deleteMovie).not.toHaveBeenCalled();
      expect(component.movieTiles.length).toBe(1);
    });

    it('should remove the tile once the delete goes through', () => {
      closesWith(true);
      api.getDownloadedMovies.and.returnValue(of([movie()]));
      component.ngOnInit();

      component.deleteMovie(component.movieTiles[0], new Event('click'));

      expect(api.deleteMovie).toHaveBeenCalledWith(1);
      expect(component.movieTiles).toEqual([]);
    });

    it('should keep the tile when the delete fails', () => {
      // Showing it as gone when it is still on disk is the worse lie.
      closesWith(true);
      api.getDownloadedMovies.and.returnValue(of([movie()]));
      api.deleteMovie.and.returnValue(throwError(() => new Error('busy')));
      component.ngOnInit();

      component.deleteMovie(component.movieTiles[0], new Event('click'));

      expect(component.movieTiles.length).toBe(1);
      expect(component.error).toContain('Them!');
    });

    it('should remove the show once the delete goes through', () => {
      closesWith(true);
      api.getDownloadedTv.and.returnValue(of([series()]));
      component.showingMovies = false;
      component.ngOnInit();

      component.deleteSeries(component.tvTiles[0], new Event('click'));

      expect(api.deleteSeries).toHaveBeenCalledWith(1);
      expect(component.tvTiles).toEqual([]);
    });

    it('should let a delete already in flight finish after destroy', () => {
      // Writes are deliberately not tied to the component's lifetime:
      // unsubscribing aborts the request, so navigating away would cancel it.
      let aborted = false;
      closesWith(true);
      api.getDownloadedMovies.and.returnValue(of([movie()]));
      api.deleteMovie.and.returnValue(new Observable<void>(() => () => { aborted = true; }));
      component.ngOnInit();
      component.deleteMovie(component.movieTiles[0], new Event('click'));

      fixture.destroy();

      expect(aborted).toBe(false);
    });
  });

  // ─── Navigation and view state ───────────────────────────────────────

  describe('view state', () => {
    it('should open a show on its own page', () => {
      api.getDownloadedTv.and.returnValue(of([series({ id: 42 })]));
      component.showingMovies = false;
      component.ngOnInit();

      component.openSeries(component.tvTiles[0]);

      expect(router.navigate).toHaveBeenCalledWith(['/media-library/series', 42]);
    });

    it('should start with the filter sheet closed on a phone', () => {
      // The grid is what the page is for; on a narrow screen the filters would
      // otherwise be all that is above the fold.
      spyOnProperty(window, 'innerWidth').and.returnValue(400);

      component.ngOnInit();

      expect(component.sidebarCollapsed).toBe(true);
    });

    it('should start with the filter sheet open on a desktop', () => {
      spyOnProperty(window, 'innerWidth').and.returnValue(1400);

      component.ngOnInit();

      expect(component.sidebarCollapsed).toBe(false);
    });

    it('should clear the selection on leaving bulk mode', () => {
      component.ngOnInit();
      component.toggleBulkEditMode();
      component.toggleTileSelection(1);

      component.toggleBulkEditMode();

      expect(component.bulkEditMode).toBe(false);
      expect(component.selectedForBulk.size).toBe(0);
    });
  });
});
