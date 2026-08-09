import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MatDialog } from '@angular/material/dialog';
import { Router } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';

import { VideoLibraryComponent } from './video-library.component';
import {
  VideoDto, VideoLibraryApiService, VideosPaginatedResponse
} from '../services/video-library-api.service';
import { AuthService } from '../services/auth.service';
import { LoggerService } from '../services/logger.service';

/**
 * Characterization tests for the downloaded-video grid.
 *
 * A review pass in the same series as the audiobook grid, the two Date Night
 * pages and the media library. It found four things, each pinned by the test
 * that carries its story: Reset restored the controls without restoring the
 * grid, the background loader could request the same page forever, a failed
 * rating save kept the new stars on screen, and all three of this page's
 * writes were routed through the read guard.
 */
describe('VideoLibraryComponent (characterization)', () => {
  let fixture: ComponentFixture<VideoLibraryComponent>;
  let component: VideoLibraryComponent;
  let api: jasmine.SpyObj<VideoLibraryApiService>;
  let dialog: jasmine.SpyObj<MatDialog>;

  // ─── Fixtures ────────────────────────────────────────────────────────

  function video(over: Partial<VideoDto> = {}): VideoDto {
    return {
      fileName: 'a.mp4',
      title: 'A Video',
      channel: 'A Channel',
      duration: '10:00',
      durationSeconds: 600,
      format: 'mp4',
      resolution: '1080p',
      fileSize: '100 MB',
      thumbnailUrl: null,
      description: null,
      primaryGenre: null,
      tags: [],
      playlist: null,
      youTubeId: null,
      personalRating: null,
      bookmarked: null,
      downloadedAt: '2026-01-01T00:00:00Z',
      publishedAt: null,
      ...over
    };
  }

  function page(videos: VideoDto[], totalCount = videos.length): VideosPaginatedResponse {
    return { videos, totalCount } as VideosPaginatedResponse;
  }

  /** Loads the grid with `videos` and no background batches to follow. */
  function loadWith(videos: VideoDto[]): void {
    api.getVideosPaginated.and.returnValue(of(page(videos)));
    component.ngOnInit();
  }

  beforeEach(async () => {
    api = jasmine.createSpyObj<VideoLibraryApiService>('VideoLibraryApiService', [
      'getVideos', 'getVideosPaginated', 'updateVideoMetadata', 'updateVideoRatings',
      'getVideoStreamUrl'
    ]);
    api.getVideosPaginated.and.returnValue(of(page([])));
    api.getVideos.and.returnValue(of([]));
    api.updateVideoMetadata.and.returnValue(of({ success: true, message: 'ok' }));
    api.updateVideoRatings.and.returnValue(of({ success: true, message: 'ok' }));
    api.getVideoStreamUrl.and.returnValue('http://x/stream');

    dialog = jasmine.createSpyObj<MatDialog>('MatDialog', ['open']);
    dialog.open.and.returnValue({ afterClosed: () => of(undefined) } as any);

    await TestBed.configureTestingModule({
      imports: [VideoLibraryComponent, NoopAnimationsModule],
      providers: [
        { provide: VideoLibraryApiService, useValue: api },
        { provide: Router, useValue: jasmine.createSpyObj('Router', ['navigate']) },
        { provide: AuthService, useValue: { getOwnerName: () => 'Paul', isAdmin: () => true } },
        { provide: LoggerService, useValue: jasmine.createSpyObj('LoggerService', ['error', 'warn', 'info', 'log', 'debug']) }
      ]
    })
      .overrideProvider(MatDialog, { useValue: dialog })
      .compileComponents();

    fixture = TestBed.createComponent(VideoLibraryComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => fixture.destroy());

  // ─── Loading ─────────────────────────────────────────────────────────

  describe('loading', () => {
    it('should show the first page before the rest arrives', () => {
      // A big library should not make the user wait on all of it.
      loadWith([video({ fileName: 'a.mp4' })]);

      expect(component.loading).toBe(false);
      expect(component.videos.length).toBe(1);
      expect(component.displayVideos.length).toBe(1);
    });

    it('should fetch the remainder in the background', () => {
      api.getVideosPaginated.and.returnValues(
        of(page([video({ fileName: 'a.mp4' })], 2)),
        of(page([video({ fileName: 'b.mp4' })], 2))
      );

      component.ngOnInit();

      expect(component.videos.map(v => v.fileName)).toEqual(['a.mp4', 'b.mp4']);
    });

    /**
     * The worst of the four.
     *
     * The loop advanced its cursor by however many videos came back, then
     * recursed while the cursor was short of the server's own totalCount. A
     * batch that came back empty advanced it by nothing — so the same page was
     * requested again, and again, with no delay and nothing to stop it. Any
     * disagreement between totalCount and what the endpoint would actually hand
     * over turned the page into an open request loop against the API.
     */
    it('should stop rather than re-request a page that came back empty', () => {
      api.getVideosPaginated.and.returnValues(
        of(page([video({ fileName: 'a.mp4' })], 500)),
        of(page([], 500)),
        // Anything past here means the loop did not stop.
        of(page([], 500)),
        of(page([], 500))
      );

      component.ngOnInit();

      expect(api.getVideosPaginated).toHaveBeenCalledTimes(2);
      expect(component.videos.map(v => v.fileName)).toEqual(['a.mp4']);
    });

    it('should keep what the background load did fetch when it fails partway', () => {
      // A partly loaded library beats an empty one.
      api.getVideosPaginated.and.returnValues(
        of(page([video({ fileName: 'a.mp4' })], 3)),
        of(page([video({ fileName: 'b.mp4' })], 3)),
        throwError(() => new Error('down'))
      );

      component.ngOnInit();

      expect(component.videos.map(v => v.fileName)).toEqual(['a.mp4', 'b.mp4']);
    });

    it('should fall back to the unpaginated endpoint', () => {
      api.getVideosPaginated.and.returnValue(throwError(() => new Error('no such route')));
      api.getVideos.and.returnValue(of([video()]));

      component.ngOnInit();

      expect(component.videos.length).toBe(1);
      expect(component.error).toBeNull();
      expect(component.loading).toBe(false);
    });

    it('should give up with a message when both endpoints fail', () => {
      api.getVideosPaginated.and.returnValue(throwError(() => new Error('down')));
      api.getVideos.and.returnValue(throwError(() => new Error('down')));

      component.ngOnInit();

      expect(component.error).toBe('Failed to load video library.');
      expect(component.loading).toBe(false);
    });

    it('should not apply a load that lands after destroy', () => {
      const late = new Subject<VideosPaginatedResponse>();
      api.getVideosPaginated.and.returnValue(late.asObservable());
      component.ngOnInit();

      fixture.destroy();
      late.next(page([video()]));

      expect(component.videos).toEqual([]);
    });

    it('should collect genres and tags into one list', () => {
      loadWith([
        video({ fileName: 'a.mp4', primaryGenre: 'Music', tags: ['Live'] }),
        video({ fileName: 'b.mp4', primaryGenre: 'Talks', tags: ['Live', 'Long'] })
      ]);

      expect(component.genres).toEqual(['Live', 'Long', 'Music', 'Talks']);
    });

    it('should start with the sidebar closed on a phone', () => {
      spyOnProperty(window, 'innerWidth').and.returnValue(400);

      component.ngOnInit();

      expect(component.sidebarCollapsed).toBe(true);
    });
  });

  // ─── Filtering ───────────────────────────────────────────────────────

  describe('filtering', () => {
    beforeEach(() => {
      loadWith([
        video({ fileName: 'a.mp4', title: 'Apollo', channel: 'NASA', primaryGenre: 'Space', tags: ['Docs'], personalRating: 5, bookmarked: true }),
        video({ fileName: 'b.mp4', title: 'Bicycles', channel: 'Shop', primaryGenre: 'DIY', tags: [], personalRating: 2, bookmarked: false }),
        video({ fileName: 'c.mp4', title: 'Comets', channel: 'NASA', primaryGenre: 'Space', tags: [], personalRating: null, bookmarked: null })
      ]);
    });

    it('should search across title, channel, genre and tags', () => {
      component.searchTerm = 'nasa';
      component.invalidateFilterCache();
      expect(component.filteredVideos.map(v => v.fileName).sort()).toEqual(['a.mp4', 'c.mp4']);

      component.searchTerm = 'docs';
      component.invalidateFilterCache();
      expect(component.filteredVideos.map(v => v.fileName)).toEqual(['a.mp4']);
    });

    it('should match a genre against the primary genre or any tag', () => {
      component.selectedGenre = 'Docs';
      component.invalidateFilterCache();

      expect(component.filteredVideos.map(v => v.fileName)).toEqual(['a.mp4']);
    });

    it('should treat a missing rating as nought', () => {
      component.minPersonalRating = 1;
      component.invalidateFilterCache();

      expect(component.filteredVideos.map(v => v.fileName).sort()).toEqual(['a.mp4', 'b.mp4']);
    });

    it('should filter to bookmarks', () => {
      component.toggleBookmarkFilter();

      expect(component.filteredVideos.map(v => v.fileName)).toEqual(['a.mp4']);
    });

    /**
     * The most visible of the four.
     *
     * resetView cleared the cache and rebuilt the grid as its *first* step, so
     * the rebuild ran against the filters it was about to clear. Every control
     * went back to its default and the grid kept showing the filtered set —
     * the one state where the page says one thing and shows another, with no
     * way to fix it except reloading.
     */
    it('should actually put the grid back, not just the controls', () => {
      component.searchTerm = 'apollo';
      component.invalidateFilterCache();
      expect(component.filteredVideos.length).toBe(1);

      component.resetView();

      expect(component.searchTerm).toBe('');
      expect(component.filteredVideos.length).toBe(3);
    });

    it('should clear every control on reset', () => {
      component.searchTerm = 'x';
      component.selectedGenre = 'Space';
      component.minPersonalRating = 3;
      component.sortOrder = 'title';
      component.sortDirection = 'up';
      component.tileSize = 'large';
      component.filterBookmarked = true;
      component.bulkEditMode = true;
      component.selectedVideosForBulk.add('a.mp4');

      component.resetView();

      expect(component.selectedGenre).toBe('');
      expect(component.minPersonalRating).toBe(0);
      expect(component.sortOrder).toBe('date');
      expect(component.sortDirection).toBe('down');
      expect(component.tileSize).toBe('medium');
      expect(component.filterBookmarked).toBe(false);
      expect(component.bulkEditMode).toBe(false);
      expect(component.selectedVideosForBulk.size).toBe(0);
    });
  });

  // ─── Sorting ─────────────────────────────────────────────────────────

  describe('sorting', () => {
    beforeEach(() => {
      loadWith([
        video({ fileName: 'a.mp4', title: 'Beta', channel: 'Zed', durationSeconds: 100, personalRating: 3, downloadedAt: '2026-01-02T00:00:00Z' }),
        video({ fileName: 'b.mp4', title: 'Alpha', channel: 'Yan', durationSeconds: 300, personalRating: 5, downloadedAt: '2026-01-03T00:00:00Z' }),
        video({ fileName: 'c.mp4', title: 'Gamma', channel: 'Xor', durationSeconds: 200, personalRating: 3, downloadedAt: '2026-01-01T00:00:00Z' })
      ]);
    });

    /**
     * "Down" means different things by design, and the arrow is why: for text
     * it reads A→Z, for numbers it reads largest first. Newest, longest and
     * best-rated are all what you want first; A-to-Z is not "Z first".
     */
    it('should read A to Z for text and largest first for numbers', () => {
      component.sortOrder = 'title';
      component.onSortChange();
      expect(component.filteredVideos.map(v => v.title)).toEqual(['Alpha', 'Beta', 'Gamma']);

      component.sortOrder = 'duration';
      component.onSortChange();
      expect(component.filteredVideos.map(v => v.durationSeconds)).toEqual([300, 200, 100]);
    });

    it('should sort by channel', () => {
      component.sortOrder = 'channel';
      component.onSortChange();

      expect(component.filteredVideos.map(v => v.channel)).toEqual(['Xor', 'Yan', 'Zed']);
    });

    it('should sort newest first by default', () => {
      expect(component.sortOrder).toBe('date');
      component.onSortChange();

      expect(component.filteredVideos.map(v => v.fileName)).toEqual(['b.mp4', 'a.mp4', 'c.mp4']);
    });

    it('should break a rating tie on title', () => {
      // Otherwise everything rated 3 shuffles about whenever the list reloads.
      component.sortOrder = 'rating';
      component.onSortChange();

      expect(component.filteredVideos.map(v => v.title)).toEqual(['Alpha', 'Beta', 'Gamma']);
    });

    it('should flip the order and keep the field', () => {
      component.sortOrder = 'title';
      component.onSortChange();

      component.toggleSortDirection();

      expect(component.sortDirection).toBe('up');
      expect(component.filteredVideos.map(v => v.title)).toEqual(['Gamma', 'Beta', 'Alpha']);
    });

    it('should go back to the default direction when the field changes', () => {
      // Carrying "up" across to a new field means the first click on Duration
      // shows the shortest video, which nobody asked for.
      component.toggleSortDirection();
      expect(component.sortDirection).toBe('up');

      component.sortOrder = 'duration';
      component.onSortChange();

      expect(component.sortDirection).toBe('down');
    });
  });

  // ─── Rating and bookmarking ──────────────────────────────────────────

  describe('rating and bookmarking', () => {
    beforeEach(() => loadWith([video({ fileName: 'a.mp4', personalRating: null })]));

    it('should save a rating', () => {
      const v = component.videos[0];

      component.onRatingChange({ video: v, rating: 4 });

      expect(v.personalRating).toBe(4);
      expect(api.updateVideoRatings).toHaveBeenCalledWith('a.mp4', { personalRating: 4 });
    });

    it('should let one star clear the rating', () => {
      // With only one star there is no lower value to drag to, so pressing the
      // star you are already on is the way back to unrated.
      const v = component.videos[0];
      v.personalRating = 1;

      component.onRatingChange({ video: v, rating: 1 });

      expect(v.personalRating).toBe(0);
    });

    it('should not save a rating that did not change', () => {
      const v = component.videos[0];
      v.personalRating = 4;

      component.onRatingChange({ video: v, rating: 4 });

      expect(api.updateVideoRatings).not.toHaveBeenCalled();
    });

    /**
     * The bookmark toggle had always put itself back when the save failed. The
     * rating did not — it left the new stars on screen, which reads as saved.
     * The next load would quietly show the old value back.
     */
    it('should put the stars back when the save fails', () => {
      const v = component.videos[0];
      v.personalRating = 2;
      api.updateVideoRatings.and.returnValue(throwError(() => new Error('nope')));

      component.onRatingChange({ video: v, rating: 5 });

      expect(v.personalRating).toBe(2);
    });

    it('should toggle a bookmark and put it back when the save fails', () => {
      const v = component.videos[0];
      api.updateVideoRatings.and.returnValue(throwError(() => new Error('nope')));

      component.onBookmarkToggle(v);

      expect(v.bookmarked).toBe(false);
    });

    /**
     * The fourth defect, and the one that had a rule written for it already:
     * unsubscribing an HttpClient call aborts the request. All three of this
     * page's writes were piped through the read guard, so rating a video and
     * navigating away in the same breath cancelled the save the UI had just
     * shown as done.
     */
    it('should let a rating save survive leaving the page', () => {
      let aborted = false;
      api.updateVideoRatings.and.returnValue(
        new Observable<any>(() => () => { aborted = true; }));
      component.onRatingChange({ video: component.videos[0], rating: 4 });

      fixture.destroy();

      expect(aborted).toBe(false);
    });

    it('should let a bookmark save survive leaving the page', () => {
      let aborted = false;
      api.updateVideoRatings.and.returnValue(
        new Observable<any>(() => () => { aborted = true; }));
      component.onBookmarkToggle(component.videos[0]);

      fixture.destroy();

      expect(aborted).toBe(false);
    });
  });

  // ─── The edit dialog ─────────────────────────────────────────────────

  describe('the edit dialog', () => {
    beforeEach(() => loadWith([
      video({ fileName: 'a.mp4', title: 'Old Title', primaryGenre: 'Music', tags: ['Live'] })
    ]));

    it('should apply an edit to the grid and save it', () => {
      dialog.open.and.returnValue({
        afterClosed: () => of({ title: 'New Title', primaryGenre: 'Talks', tags: ['Long'] })
      } as any);

      component.openEditDialog(component.videos[0]);

      expect(component.videos[0].title).toBe('New Title');
      expect(component.genres).toEqual(['Long', 'Talks']);
      expect(api.updateVideoMetadata).toHaveBeenCalled();
    });

    it('should change nothing when the dialog is cancelled', () => {
      dialog.open.and.returnValue({ afterClosed: () => of(undefined) } as any);

      component.openEditDialog(component.videos[0]);

      expect(component.videos[0].title).toBe('Old Title');
      expect(api.updateVideoMetadata).not.toHaveBeenCalled();
    });

    it('should drop a video the dialog deleted', () => {
      dialog.open.and.returnValue({ afterClosed: () => of({ deleted: true }) } as any);

      component.openEditDialog(component.videos[0]);

      expect(component.videos).toEqual([]);
      expect(component.filteredVideos).toEqual([]);
      // The dialog has already done the deleting; re-saving metadata for a
      // file that is gone would be a 404 at best.
      expect(api.updateVideoMetadata).not.toHaveBeenCalled();
    });

    it('should let a metadata save survive leaving the page', () => {
      let aborted = false;
      dialog.open.and.returnValue({ afterClosed: () => of({ title: 'New Title' }) } as any);
      api.updateVideoMetadata.and.returnValue(
        new Observable<any>(() => () => { aborted = true; }));
      component.openEditDialog(component.videos[0]);

      fixture.destroy();

      expect(aborted).toBe(false);
    });

    it('should open the player with a stream URL for the right file', () => {
      component.onPlayClick(component.videos[0]);

      expect(api.getVideoStreamUrl).toHaveBeenCalledWith('a.mp4');
      expect(dialog.open).toHaveBeenCalled();
    });
  });

  // ─── Layout and bulk selection ───────────────────────────────────────

  describe('layout', () => {
    beforeEach(() => loadWith(
      Array.from({ length: 7 }, (_, i) => video({ fileName: `${i}.mp4`, title: `V${i}` }))
    ));

    it('should pack the grid into rows of the size the tiles are', () => {
      // The virtual scroller measures rows, not tiles, so the row width has to
      // follow the tile size or the last row is short and the viewport is wrong.
      component.setTileSize('large');
      component.recalculateLayout();
      expect(component.videoRows.map(r => r.length)).toEqual([3, 3, 1]);

      component.setTileSize('small');
      component.recalculateLayout();
      expect(component.videoRows.map(r => r.length)).toEqual([6, 1]);
    });

    it('should give each tile size its own row height', () => {
      component.tileSize = 'small';
      expect(component.rowHeight).toBe(220);
      component.tileSize = 'medium';
      expect(component.rowHeight).toBe(280);
      component.tileSize = 'large';
      expect(component.rowHeight).toBe(340);
    });

    it('should track rows and tiles by something stable', () => {
      // Falling back to the index would re-render every tile on any reorder.
      expect(component.trackByFileName(0, video({ fileName: 'a.mp4' }))).toBe('a.mp4');
      expect(component.trackByRow(0, [video({ fileName: 'a.mp4' })])).toBe('a.mp4');
    });
  });

  describe('bulk selection', () => {
    beforeEach(() => {
      loadWith([
        video({ fileName: 'a.mp4', title: 'Apollo' }),
        video({ fileName: 'b.mp4', title: 'Bicycles' })
      ]);
      component.toggleBulkEditMode();
    });

    it('should ignore tile clicks outside bulk mode', () => {
      component.toggleBulkEditMode();

      component.toggleVideoSelection(component.videos[0]);

      expect(component.selectedVideosForBulk.size).toBe(0);
    });

    it('should select and deselect', () => {
      component.toggleVideoSelection(component.videos[0]);
      expect(component.selectedVideosForBulk.has('a.mp4')).toBe(true);

      component.toggleVideoSelection(component.videos[0]);
      expect(component.selectedVideosForBulk.has('a.mp4')).toBe(false);
    });

    it('should select what is visible, not what exists', () => {
      component.searchTerm = 'apollo';
      component.invalidateFilterCache();

      component.selectAllVisible();

      expect([...component.selectedVideosForBulk]).toEqual(['a.mp4']);
    });

    it('should drop the selection on leaving bulk mode', () => {
      component.toggleVideoSelection(component.videos[0]);

      component.toggleBulkEditMode();

      expect(component.selectedVideosForBulk.size).toBe(0);
    });
  });
});
