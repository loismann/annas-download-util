import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MatDialog } from '@angular/material/dialog';
import { ActivatedRoute } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';

import { AudiobooksComponent } from './audiobooks.component';
import { AudiobookApiService, AudiobookItem } from '../services/audiobook-api.service';
import { AudiobookRequestApiService, AudiobookRequestStatus } from '../services/audiobook-request-api.service';
import { AuthService } from '../services/auth.service';
import { LoggerService } from '../services/logger.service';

/**
 * Characterization tests for the audiobook library.
 *
 * Written before any restructuring of this component, for the reason recorded in
 * ASSERTIONS: the four structural passes over the book reader found none of its
 * defects, and a single review pass found all of them. This suite is that review
 * pass for the audiobook grid, and it found one — see "polling lifecycle".
 */
describe('AudiobooksComponent (characterization)', () => {
  let fixture: ComponentFixture<AudiobooksComponent>;
  let component: AudiobooksComponent;
  let api: jasmine.SpyObj<AudiobookApiService>;
  let requestApi: jasmine.SpyObj<AudiobookRequestApiService>;

  /**
   * Audiobookshelf's own shape, not a flattened one: the component reads
   * `media.metadata.*` and rides `addedAt` through as raw epoch milliseconds.
   * The fixture has to match, or the tests pin a library that does not exist.
   */
  function book(fields: {
    id?: string;
    title?: string;
    author?: string;
    narrator?: string;
    series?: string;
    genres?: string[];
    owners?: string[];
    favorites?: string[];
    addedAt?: number;
  } = {}): AudiobookItem {
    return {
      id: fields.id ?? 'id-1',
      customGenres: fields.genres ?? [],
      owners: fields.owners ?? [],
      favorites: fields.favorites ?? [],
      addedAt: fields.addedAt ?? 0,
      media: {
        metadata: {
          title: fields.title ?? 'A Title',
          authorName: fields.author ?? 'An Author',
          narratorName: fields.narrator ?? 'A Narrator',
          seriesName: fields.series ?? ''
        }
      }
    } as unknown as AudiobookItem;
  }

  function request(over: Partial<AudiobookRequestStatus> = {}): AudiobookRequestStatus {
    return {
      listenarrId: 1,
      title: 'Pending Book',
      state: 'Downloading',
      progress: 42,
      totalSize: 1048576,
      error: undefined,
      importBlockMessages: [],
      ...over
    } as unknown as AudiobookRequestStatus;
  }

  beforeEach(async () => {
    api = jasmine.createSpyObj<AudiobookApiService>('AudiobookApiService', ['getCatalog', 'setFavorite']);
    requestApi = jasmine.createSpyObj<AudiobookRequestApiService>(
      'AudiobookRequestApiService', ['listMyRequests', 'dismissRequest', 'retryImport']);

    api.getCatalog.and.returnValue(of([]));
    api.setFavorite.and.returnValue(of({ favorites: [] } as any));
    requestApi.listMyRequests.and.returnValue(of([]));
    requestApi.dismissRequest.and.returnValue(of(void 0 as any));
    requestApi.retryImport.and.returnValue(of(void 0 as any));

    await TestBed.configureTestingModule({
      imports: [AudiobooksComponent, NoopAnimationsModule],
      providers: [
        { provide: AudiobookApiService, useValue: api },
        { provide: AudiobookRequestApiService, useValue: requestApi },
        { provide: AuthService, useValue: { getOwnerName: () => null } },
        { provide: LoggerService, useValue: jasmine.createSpyObj('LoggerService', ['error', 'warn', 'info', 'debug']) },
        { provide: MatDialog, useValue: jasmine.createSpyObj('MatDialog', ['open']) },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: new Map() } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(AudiobooksComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => fixture.destroy());

  // ─── Polling lifecycle ───────────────────────────────────────────────

  describe('polling lifecycle', () => {
    /**
     * The bug this was written for.
     *
     * `loadPendingRequests` polls while anything is still downloading, and
     * `ngOnDestroy` clears the timer. But the fetch it is waiting on is not tied
     * to the component's lifetime, so a response that lands *after* destroy
     * still runs `syncRequestPolling`, which sees a null timer and starts a new
     * interval — on a component that no longer exists. Nothing ever clears that
     * one, and every navigation back to the page adds another.
     */
    it('should not resume polling from a response that arrives after destroy', () => {
      const late = new Subject<AudiobookRequestStatus[]>();
      requestApi.listMyRequests.and.returnValue(late.asObservable());
      const setInterval = spyOn(window, 'setInterval').and.callThrough();

      component.ngOnInit();
      fixture.destroy();

      // The in-flight request completes after the component is gone, reporting
      // an active download — which is what would restart the timer.
      late.next([request({ state: 'Downloading' })]);

      expect(setInterval).not.toHaveBeenCalled();
    });

    it('should not keep fetching after destroy', () => {
      const late = new Subject<AudiobookRequestStatus[]>();
      requestApi.listMyRequests.and.returnValue(late.asObservable());

      component.ngOnInit();
      const callsAtDestroy = requestApi.listMyRequests.calls.count();
      fixture.destroy();
      late.next([request({ state: 'Downloading' })]);

      expect(requestApi.listMyRequests.calls.count()).toBe(callsAtDestroy);
    });

    it('should poll while a request is still moving', () => {
      const setInterval = spyOn(window, 'setInterval').and.callThrough();
      requestApi.listMyRequests.and.returnValue(of([request({ state: 'Downloading' })]));

      component.ngOnInit();

      expect(setInterval).toHaveBeenCalled();
    });

    it('should let a mutation already in flight finish after destroy', () => {
      // Only *reads* are tied to the component's lifetime. Unsubscribing an
      // HttpClient call aborts the underlying request, so guarding a write the
      // same way would mean navigating away cancelled the user's save.
      let aborted = false;
      requestApi.dismissRequest.and.returnValue(
        new Observable<any>(() => () => { aborted = true; }));
      component.pendingRequests = [request({ listenarrId: 7 })];
      component.dismissRequest(component.pendingRequests[0], new Event('click'));

      fixture.destroy();

      expect(aborted).toBe(false);
    });

    it('should not poll when everything has settled', () => {
      const setInterval = spyOn(window, 'setInterval').and.callThrough();
      requestApi.listMyRequests.and.returnValue(of([request({ state: 'Failed' })]));

      component.ngOnInit();

      expect(setInterval).not.toHaveBeenCalled();
    });
  });

  // ─── Pending request presentation ────────────────────────────────────

  describe('pending requests', () => {
    it('should clamp progress into 0-100', () => {
      expect(component.requestProgressPercent(request({ progress: -5 }))).toBe(0);
      expect(component.requestProgressPercent(request({ progress: 250 }))).toBe(100);
      expect(component.requestProgressPercent(request({ progress: undefined }))).toBe(0);
    });

    it('should show size alongside percent only while downloading', () => {
      expect(component.requestSubtitle(request({ state: 'Downloading', progress: 50, totalSize: 1048576 })))
        .toContain('50%');
      expect(component.requestSubtitle(request({ state: 'Queued', progress: 50 })))
        .not.toContain('50%');
    });

    it('should treat Failed and ImportBlocked as failures', () => {
      expect(component.isRequestFailed(request({ state: 'Failed' }))).toBe(true);
      expect(component.isRequestFailed(request({ state: 'ImportBlocked' }))).toBe(true);
      expect(component.isRequestFailed(request({ state: 'Downloading' }))).toBe(false);
    });

    it('should prefer the error, then the import block reason, for the tooltip', () => {
      expect(component.requestTooltip(request({ error: 'disk full' }))).toBe('disk full');
      expect(component.requestTooltip(request({ error: undefined, importBlockMessages: ['no matching file'] })))
        .toBe('no matching file');
      expect(component.requestTooltip(request({ error: undefined, importBlockMessages: [] })))
        .toContain('Not downloaded yet');
    });

    it('should filter ghosts by the search box but not by the metadata filters', () => {
      // A book that has not downloaded has no genre or owner yet, so applying
      // those filters would always hide it.
      component.pendingRequests = [request({ title: 'Dune' }), request({ listenarrId: 2, title: 'Neuromancer' })];
      component.selectedGenre = 'Sci-Fi';
      component.filterFavoritesOnly = true;

      expect(component.visiblePendingRequests.length).toBe(2);

      component.searchTerm = 'dune';
      expect(component.visiblePendingRequests.map(r => r.title)).toEqual(['Dune']);
    });
  });

  describe('dismissing a request', () => {
    it('should remove it immediately', () => {
      component.pendingRequests = [request({ listenarrId: 7 })];

      component.dismissRequest(component.pendingRequests[0], new Event('click'));

      expect(component.pendingRequests).toEqual([]);
    });

    it('should put it back when the server refuses', () => {
      // Better to show the request again than to claim it is gone when it isn't.
      requestApi.dismissRequest.and.returnValue(throwError(() => new Error('nope')));
      const item = request({ listenarrId: 7 });
      component.pendingRequests = [item];

      component.dismissRequest(item, new Event('click'));

      expect(component.pendingRequests).toEqual([item]);
    });
  });

  // ─── The grid ────────────────────────────────────────────────────────

  describe('filtering and sorting', () => {
    beforeEach(() => {
      component.items = [
        book({ id: 'b', title: 'Banana', author: 'Zoe', addedAt: 3000 }),
        book({ id: 'a', title: 'Apple', author: 'Yara', addedAt: 1000 }),
        book({ id: 'c', title: 'Cherry', author: 'Xavier', addedAt: 2000 })
      ];
    });

    it('should sort newest first by default', () => {
      expect(component.sortOrder).toBe('recent');
      expect(component.filteredItems.map(i => i.id)).toEqual(['b', 'c', 'a']);
    });

    it('should sort by title and by author', () => {
      component.sortOrder = 'title';
      expect(component.filteredItems.map(i => i.media?.metadata?.title)).toEqual(['Apple', 'Banana', 'Cherry']);

      component.sortOrder = 'author';
      expect(component.filteredItems.map(i => i.media?.metadata?.authorName)).toEqual(['Xavier', 'Yara', 'Zoe']);
    });

    it('should search across title, author and narrator', () => {
      component.searchTerm = 'yara';
      expect(component.filteredItems.map(i => i.id)).toEqual(['a']);
    });

    it('should report counts before and after filtering', () => {
      component.searchTerm = 'apple';

      expect(component.totalCount).toBe(3);
      expect(component.filteredCount).toBe(1);
    });

    it('should list every genre once, sorted', () => {
      component.items = [
        book({ id: '1', genres: ['Sci-Fi', 'Horror'] }),
        book({ id: '2', genres: ['Horror'] })
      ];

      expect(component.genres).toEqual(['Horror', 'Sci-Fi']);
    });
  });

  describe('loading the catalog', () => {
    it('should surface a reachability message rather than a stack', () => {
      api.getCatalog.and.returnValue(throwError(() => new Error('ECONNREFUSED')));

      component.ngOnInit();

      expect(component.error).toContain('Audiobookshelf');
      expect(component.loading).toBe(false);
    });

    it('should render the library even when the request service is down', () => {
      // Two independent backends: a Listenarr outage must not blank the
      // playable library that came from Audiobookshelf.
      requestApi.listMyRequests.and.returnValue(throwError(() => new Error('down')));
      api.getCatalog.and.returnValue(of([book({ id: 'x' })]));

      component.ngOnInit();

      expect(component.items.length).toBe(1);
      expect(component.error).toBeNull();
      expect(component.pendingRequests).toEqual([]);
    });
  });
});
