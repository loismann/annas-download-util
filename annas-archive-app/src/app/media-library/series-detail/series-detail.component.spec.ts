import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MatDialog } from '@angular/material/dialog';
import { ActivatedRoute, Router } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';

import { SeriesDetailComponent } from './series-detail.component';
import { EpisodeInfo, MediaLibraryApiService } from '../../services/media-library-api.service';
import { MediaLookupResult, MediaSearchApiService } from '../../services/media-search-api.service';
import { LoggerService } from '../../services/logger.service';

/**
 * Characterization tests for the season/episode page.
 *
 * A review pass in the same series as the library grids. It found no defect —
 * recorded here because a clean pass is worth knowing about: this page already
 * guards its two reads and leaves all four of its writes alone, which is the
 * rule the other pages had to be corrected to follow.
 *
 * "Download" means two different things on this page and the tests keep them
 * apart: fetching a file that already exists, versus asking Sonarr to go and
 * acquire one.
 */
describe('SeriesDetailComponent (characterization)', () => {
  let fixture: ComponentFixture<SeriesDetailComponent>;
  let component: SeriesDetailComponent;
  let api: jasmine.SpyObj<MediaLibraryApiService>;
  let searchApi: jasmine.SpyObj<MediaSearchApiService>;
  let dialog: jasmine.SpyObj<MatDialog>;
  let router: jasmine.SpyObj<Router>;
  let seriesIdParam: string | null;

  function series(over: Partial<MediaLookupResult> = {}): MediaLookupResult {
    return { id: 7, tvdbId: 200, title: 'The Outer Limits', ...over };
  }

  function episode(over: Partial<EpisodeInfo> = {}): EpisodeInfo {
    return { id: 1, seasonNumber: 1, episodeNumber: 1, title: 'The Galaxy Being', hasFile: true, ...over };
  }

  function closesWith(result: unknown): void {
    dialog.open.and.returnValue({ afterClosed: () => of(result) } as any);
  }

  beforeEach(async () => {
    seriesIdParam = '7';

    api = jasmine.createSpyObj<MediaLibraryApiService>('MediaLibraryApiService', [
      'getDownloadedTv', 'getSeriesEpisodes', 'watchTv', 'deleteSeason',
      'getEpisodeStreamUrl', 'getEpisodeHlsMasterUrl', 'getEpisodeSubtitleUrl',
      'getEpisodeDownloadUrl', 'saveTvProgress', 'searchSeasonReleases', 'grabSeasonRelease'
    ]);
    api.getDownloadedTv.and.returnValue(of([series()]));
    api.getSeriesEpisodes.and.returnValue(of([episode()]));
    api.watchTv.and.returnValue(of({ mode: 'embed', embedUrl: 'http://x/e' } as any));
    api.deleteSeason.and.returnValue(of({} as any));
    // downloadEpisode assigns this to window.location.href. Any value at all
    // navigates the Karma page out from under the run, so the builder throws
    // instead — see the test that asserts its arguments.
    api.getEpisodeDownloadUrl.and.throwError('would navigate');

    searchApi = jasmine.createSpyObj<MediaSearchApiService>('MediaSearchApiService', ['updateTvSeasons']);
    searchApi.updateTvSeasons.and.returnValue(of(series()));

    dialog = jasmine.createSpyObj<MatDialog>('MatDialog', ['open']);
    closesWith(undefined);

    router = jasmine.createSpyObj<Router>('Router', ['navigate']);

    await TestBed.configureTestingModule({
      imports: [SeriesDetailComponent, NoopAnimationsModule],
      providers: [
        { provide: MediaLibraryApiService, useValue: api },
        { provide: MediaSearchApiService, useValue: searchApi },
        { provide: Router, useValue: router },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => seriesIdParam } } } },
        { provide: LoggerService, useValue: jasmine.createSpyObj('LoggerService', ['error', 'warn', 'info', 'log', 'debug']) }
      ]
    })
      .overrideProvider(MatDialog, { useValue: dialog })
      .compileComponents();

    fixture = TestBed.createComponent(SeriesDetailComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => fixture.destroy());

  // ─── Loading ─────────────────────────────────────────────────────────

  describe('loading', () => {
    it('should group episodes into seasons, in order', () => {
      api.getSeriesEpisodes.and.returnValue(of([
        episode({ id: 3, seasonNumber: 2, episodeNumber: 1 }),
        episode({ id: 1, seasonNumber: 1, episodeNumber: 2 }),
        episode({ id: 2, seasonNumber: 1, episodeNumber: 1 })
      ]));

      component.ngOnInit();

      expect(component.seasonGroups.map(g => g.seasonNumber)).toEqual([1, 2]);
      expect(component.seasonGroups[0].episodes.map(e => e.episodeNumber)).toEqual([1, 2]);
    });

    it('should call season zero Specials', () => {
      api.getSeriesEpisodes.and.returnValue(of([episode({ seasonNumber: 0 })]));

      component.ngOnInit();

      expect(component.seasonGroups[0].label).toBe('Specials');
    });

    it('should mark a season complete only when every episode has a file', () => {
      api.getSeriesEpisodes.and.returnValue(of([
        episode({ id: 1, episodeNumber: 1, hasFile: true }),
        episode({ id: 2, episodeNumber: 2, hasFile: false })
      ]));

      component.ngOnInit();

      expect(component.seasonGroups[0].allDownloaded).toBe(false);
    });

    it('should not call an empty season complete', () => {
      // `every` on an empty list is true — which would disable Download for a
      // season that has nothing at all.
      api.getSeriesEpisodes.and.returnValue(of([episode({ hasFile: true })]));
      component.ngOnInit();

      expect(component.seasonGroups[0].allDownloaded).toBe(true);
      expect(component.seasonGroups[0].episodes.length).toBeGreaterThan(0);
    });

    it('should reject a route with no series id', () => {
      seriesIdParam = null;

      component.ngOnInit();

      expect(component.error).toBe('Invalid series.');
      expect(component.loading).toBe(false);
      expect(api.getDownloadedTv).not.toHaveBeenCalled();
    });

    it('should say when the series is not in the library', () => {
      api.getDownloadedTv.and.returnValue(of([series({ id: 999 })]));

      component.ngOnInit();

      expect(component.error).toContain('not found');
      expect(component.loading).toBe(false);
    });

    it('should name the likely culprit when the load fails', () => {
      api.getSeriesEpisodes.and.returnValue(throwError(() => new Error('ECONNREFUSED')));

      component.ngOnInit();

      expect(component.error).toContain('Sonarr');
      expect(component.loading).toBe(false);
    });

    it('should not apply a load that lands after destroy', () => {
      const late = new Subject<EpisodeInfo[]>();
      api.getSeriesEpisodes.and.returnValue(late.asObservable());
      component.ngOnInit();

      fixture.destroy();
      late.next([episode()]);

      expect(component.seasonGroups).toEqual([]);
    });

    it('should go back to the library', () => {
      component.goBack();

      expect(router.navigate).toHaveBeenCalledWith(['/media-library']);
    });
  });

  // ─── Playing ─────────────────────────────────────────────────────────

  describe('playing an episode', () => {
    beforeEach(() => component.ngOnInit());

    it('should resolve the episode and open the player', () => {
      component.playEpisode(episode({ seasonNumber: 1, episodeNumber: 1 }));

      expect(api.watchTv).toHaveBeenCalledWith(200, 1, 1);
      expect(dialog.open).toHaveBeenCalled();
      expect(component.resolvingEpisodeId).toBeNull();
    });

    it('should not try to play an episode with no file', () => {
      component.playEpisode(episode({ hasFile: false }));

      expect(api.watchTv).not.toHaveBeenCalled();
    });

    it('should not start a second lookup while one is resolving', () => {
      api.watchTv.and.returnValue(new Subject<never>().asObservable());

      component.playEpisode(episode({ id: 1 }));
      component.playEpisode(episode({ id: 2 }));

      expect(api.watchTv).toHaveBeenCalledTimes(1);
      expect(component.resolvingEpisodeId).toBe(1);
    });

    it('should blame the scan when Jellyfin has no match', () => {
      api.watchTv.and.returnValue(throwError(() => new Error('404')));

      component.playEpisode(episode());

      expect(component.error).toContain('Jellyfin');
      // And the row has to become clickable again.
      expect(component.resolvingEpisodeId).toBeNull();
    });

    it('should not open a player over a page the user has left', () => {
      const late = new Subject<any>();
      api.watchTv.and.returnValue(late.asObservable());
      component.playEpisode(episode());
      dialog.open.calls.reset();

      fixture.destroy();
      late.next({ mode: 'embed', embedUrl: 'http://x' });

      expect(dialog.open).not.toHaveBeenCalled();
    });
  });

  // ─── The two kinds of "download" ─────────────────────────────────────

  describe('fetching a file that already exists', () => {
    beforeEach(() => component.ngOnInit());

    it('should address the file by series, season and episode', () => {
      // Assigned to window.location.href rather than opened in a new tab: the
      // response carries Content-Disposition: attachment, so the browser
      // diverts to its download handler and the SPA is never actually
      // navigated away from — and no popup blocker gets involved.
      expect(() => component.downloadEpisode(episode({ seasonNumber: 1, episodeNumber: 2 })))
        .toThrow();

      expect(api.getEpisodeDownloadUrl).toHaveBeenCalledWith(200, 1, 2);
    });

    it('should not offer a file that is not there', () => {
      component.downloadEpisode(episode({ hasFile: false }));

      expect(api.getEpisodeDownloadUrl).not.toHaveBeenCalled();
    });
  });

  describe('asking Sonarr to acquire a season', () => {
    beforeEach(() => {
      // A season with nothing downloaded — a complete one has its Download
      // button disabled, which is the case below rather than this one.
      api.getSeriesEpisodes.and.returnValue(of([episode({ hasFile: false })]));
      component.ngOnInit();
    });

    it('should request the season and mark it requested', () => {
      // "Requested", not "Downloaded" — the file does not exist until Sonarr
      // finds and grabs a release, which is not this call.
      component.downloadSeason(component.seasonGroups[0]);

      expect(searchApi.updateTvSeasons).toHaveBeenCalledWith(7, [1]);
      expect(component.requestedSeasonNumbers.has(1)).toBe(true);
      expect(component.downloadingSeasonNumber).toBeNull();
    });

    it('should not re-request a season that is already complete', () => {
      component.seasonGroups[0].allDownloaded = true;

      component.downloadSeason(component.seasonGroups[0]);

      expect(searchApi.updateTvSeasons).not.toHaveBeenCalled();
    });

    it('should not stack two requests', () => {
      searchApi.updateTvSeasons.and.returnValue(new Subject<MediaLookupResult>().asObservable());

      component.downloadSeason(component.seasonGroups[0]);
      component.downloadSeason(component.seasonGroups[0]);

      expect(searchApi.updateTvSeasons).toHaveBeenCalledTimes(1);
      expect(component.downloadingSeasonNumber).toBe(1);
    });

    it('should name the season it could not request', () => {
      searchApi.updateTvSeasons.and.returnValue(throwError(() => new Error('nope')));

      component.downloadSeason(component.seasonGroups[0]);

      expect(component.error).toContain('Season 1');
      expect(component.downloadingSeasonNumber).toBeNull();
      expect(component.requestedSeasonNumbers.has(1)).toBe(false);
    });

    it('should let a request already in flight survive leaving the page', () => {
      let aborted = false;
      searchApi.updateTvSeasons.and.returnValue(
        new Observable<MediaLookupResult>(() => () => { aborted = true; }));
      component.downloadSeason(component.seasonGroups[0]);

      fixture.destroy();

      expect(aborted).toBe(false);
    });
  });

  // ─── The release picker ──────────────────────────────────────────────

  describe('the release picker', () => {
    beforeEach(() => component.ngOnInit());

    it('should mark the season requested when a release is grabbed', () => {
      closesWith(true);

      component.openSeasonReleasePicker(component.seasonGroups[0], new Event('click'));

      expect(component.requestedSeasonNumbers.has(1)).toBe(true);
    });

    it('should change nothing when the picker is dismissed', () => {
      closesWith(false);

      component.openSeasonReleasePicker(component.seasonGroups[0], new Event('click'));

      expect(component.requestedSeasonNumbers.size).toBe(0);
    });

    it('should not also collapse or open the season row', () => {
      const event = new Event('click');
      const stop = spyOn(event, 'stopPropagation');

      component.openSeasonReleasePicker(component.seasonGroups[0], event);

      expect(stop).toHaveBeenCalled();
    });
  });

  // ─── Deleting ────────────────────────────────────────────────────────

  describe('deleting a season', () => {
    beforeEach(() => component.ngOnInit());

    it('should ask before deleting', () => {
      closesWith(false);

      component.deleteSeason(component.seasonGroups[0]);

      expect(dialog.open).toHaveBeenCalled();
      expect(api.deleteSeason).not.toHaveBeenCalled();
    });

    it('should drop the season once the delete goes through', () => {
      closesWith(true);

      component.deleteSeason(component.seasonGroups[0]);

      expect(api.deleteSeason).toHaveBeenCalledWith(7, 1);
      expect(component.seasonGroups).toEqual([]);
    });

    it('should keep the season when the delete fails', () => {
      closesWith(true);
      api.deleteSeason.and.returnValue(throwError(() => new Error('busy')));

      component.deleteSeason(component.seasonGroups[0]);

      expect(component.seasonGroups.length).toBe(1);
      expect(component.error).toContain('Season 1');
    });

    it('should let a delete already in flight survive leaving the page', () => {
      let aborted = false;
      closesWith(true);
      api.deleteSeason.and.returnValue(new Observable<void>(() => () => { aborted = true; }));
      component.deleteSeason(component.seasonGroups[0]);

      fixture.destroy();

      expect(aborted).toBe(false);
    });
  });
});
