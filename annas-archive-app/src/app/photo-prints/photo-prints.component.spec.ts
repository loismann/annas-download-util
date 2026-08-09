import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { Subject, of, throwError } from 'rxjs';

import { PhotoPrintsComponent } from './photo-prints.component';
import {
  PhotoAsset, PhotoPage, PhotoPrintApiService, PhotoPrintStatus, PrintSizeOption
} from '../services/photo-print-api.service';
import { LoggerService } from '../services/logger.service';

/**
 * Characterization tests for the photo print ordering page.
 *
 * A review pass in the same series as the library pages. It found the paging
 * race in "browsing": `Load more` appends, and a page still in flight when the
 * filters changed was appended to the grid that replaced it — last year's
 * photos landing in a list showing last week.
 */
describe('PhotoPrintsComponent (characterization)', () => {
  let fixture: ComponentFixture<PhotoPrintsComponent>;
  let component: PhotoPrintsComponent;
  let api: jasmine.SpyObj<PhotoPrintApiService>;

  // ─── Fixtures ────────────────────────────────────────────────────────

  function asset(over: Partial<PhotoAsset> = {}): PhotoAsset {
    return {
      id: 'a1', fileName: 'IMG_0001.jpg', takenAt: '2026-01-01T00:00:00Z',
      width: 4000, height: 3000, isFavorite: false, ...over
    };
  }

  function page(items: PhotoAsset[], nextPage: number | null = null, total = items.length): PhotoPage {
    return { items, nextPage, total };
  }

  function status(over: Partial<PhotoPrintStatus> = {}): PhotoPrintStatus {
    return { configured: true, reachable: true, pickupZip: '96815', maxPrintsPerRun: 50, ...over };
  }

  const SIZES: PrintSizeOption[] = [
    { code: '4x6', name: '4×6', shortInches: 4, longInches: 6, isSquare: false },
    { code: '8x10', name: '8×10', shortInches: 8, longInches: 10, isSquare: false },
    { code: '8x8', name: '8×8', shortInches: 8, longInches: 8, isSquare: true }
  ];

  beforeEach(async () => {
    api = jasmine.createSpyObj<PhotoPrintApiService>('PhotoPrintApiService', [
      'getStatus', 'getSizes', 'browsePhotos', 'thumbnailUrl', 'createRun', 'addItem', 'prepare'
    ]);
    api.getStatus.and.returnValue(of(status()));
    api.getSizes.and.returnValue(of(SIZES));
    api.browsePhotos.and.returnValue(of(page([])));
    api.thumbnailUrl.and.callFake((id: string) => `http://x/thumb/${id}`);
    api.createRun.and.returnValue(of({ runId: 'run-1' }));
    api.addItem.and.returnValue(of({ runId: 'run-1' }));
    api.prepare.and.returnValue(of({ runId: 'run-1', prepared: 1, failed: 0, belowQualityFloor: 0 }));

    await TestBed.configureTestingModule({
      imports: [PhotoPrintsComponent, NoopAnimationsModule],
      providers: [
        { provide: PhotoPrintApiService, useValue: api },
        { provide: LoggerService, useValue: jasmine.createSpyObj('LoggerService', ['error', 'warn', 'info', 'log', 'debug']) }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(PhotoPrintsComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => fixture.destroy());

  // ─── Starting up ─────────────────────────────────────────────────────

  describe('starting up', () => {
    it('should load photos once the service answers', () => {
      component.ngOnInit();

      expect(component.status?.configured).toBe(true);
      expect(api.browsePhotos).toHaveBeenCalled();
      expect(component.sizes.length).toBe(3);
    });

    it('should not browse when the service is not configured', () => {
      api.getStatus.and.returnValue(of(status({ configured: false })));

      component.ngOnInit();

      expect(api.browsePhotos).not.toHaveBeenCalled();
    });

    it('should not browse when the service is unreachable', () => {
      api.getStatus.and.returnValue(of(status({ reachable: false })));

      component.ngOnInit();

      expect(api.browsePhotos).not.toHaveBeenCalled();
    });

    it('should say so when the status call itself fails', () => {
      api.getStatus.and.returnValue(throwError(() => new Error('down')));

      component.ngOnInit();

      expect(component.error).toContain('photo print service');
    });

    it('should still work when the size list fails to load', () => {
      // Sizes drive a dropdown; losing them must not take the grid with them.
      api.getSizes.and.returnValue(throwError(() => new Error('down')));

      component.ngOnInit();

      expect(component.sizes).toEqual([]);
      expect(api.browsePhotos).toHaveBeenCalled();
    });

    it('should not apply a load that lands after destroy', () => {
      const late = new Subject<PhotoPage>();
      api.browsePhotos.and.returnValue(late.asObservable());
      component.ngOnInit();

      fixture.destroy();
      late.next(page([asset()]));

      expect(component.photos).toEqual([]);
    });
  });

  // ─── Browsing ────────────────────────────────────────────────────────

  describe('browsing', () => {
    it('should translate each range into a start date', () => {
      component.ngOnInit();

      component.dateRange = 'week';
      component.onFiltersChanged();
      const week = api.browsePhotos.calls.mostRecent().args[0]!.takenAfter!;

      component.dateRange = 'year';
      component.onFiltersChanged();
      const year = api.browsePhotos.calls.mostRecent().args[0]!.takenAfter!;

      expect(new Date(week).getTime()).toBeGreaterThan(new Date(year).getTime());
    });

    it('should send no start date for Everything', () => {
      component.ngOnInit();
      component.dateRange = 'all';

      component.onFiltersChanged();

      expect(api.browsePhotos.calls.mostRecent().args[0]!.takenAfter).toBeUndefined();
    });

    it('should pass the favourites filter through', () => {
      component.ngOnInit();
      component.favoritesOnly = true;

      component.onFiltersChanged();

      expect(api.browsePhotos.calls.mostRecent().args[0]!.favoritesOnly).toBe(true);
    });

    it('should append the next page rather than replace', () => {
      api.browsePhotos.and.returnValue(of(page([asset({ id: 'a1' })], 2, 2)));
      component.ngOnInit();

      api.browsePhotos.and.returnValue(of(page([asset({ id: 'a2' })], null, 2)));
      component.loadMore();

      expect(component.photos.map(p => p.id)).toEqual(['a1', 'a2']);
      expect(component.nextPage).toBeNull();
    });

    it('should not load more when there is no more', () => {
      api.browsePhotos.and.returnValue(of(page([asset()], null)));
      component.ngOnInit();
      const before = api.browsePhotos.calls.count();

      component.loadMore();

      expect(api.browsePhotos.calls.count()).toBe(before);
    });

    it('should not stack two Load mores', () => {
      api.browsePhotos.and.returnValue(of(page([asset()], 2, 10)));
      component.ngOnInit();
      api.browsePhotos.and.returnValue(new Subject<PhotoPage>().asObservable());

      component.loadMore();
      component.loadMore();

      expect(api.browsePhotos.calls.count()).toBe(2);
    });

    /**
     * The defect this pass found.
     *
     * `Load more` appends. Changing the filters replaces. A page still in
     * flight when the filters changed therefore landed *after* the replacement
     * and appended itself to it — putting photos from the old range into a grid
     * that said it was showing the new one, with no way to tell which was which.
     */
    it('should discard a page that arrives after the filters moved on', () => {
      api.browsePhotos.and.returnValue(of(page([asset({ id: 'old-1' })], 2, 10)));
      component.ngOnInit();

      const inFlight = new Subject<PhotoPage>();
      api.browsePhotos.and.returnValue(inFlight.asObservable());
      component.loadMore();

      // The user switches range while that page is still coming.
      api.browsePhotos.and.returnValue(of(page([asset({ id: 'new-1' })], null, 1)));
      component.dateRange = 'year';
      component.onFiltersChanged();

      inFlight.next(page([asset({ id: 'old-2' })], 3, 10));

      expect(component.photos.map(p => p.id)).toEqual(['new-1']);
      expect(component.nextPage).toBeNull();
    });

    it('should say what is likely wrong when browsing fails', () => {
      api.browsePhotos.and.returnValue(throwError(() => new Error('ECONNREFUSED')));

      component.ngOnInit();

      expect(component.error).toContain('Immich');
      expect(component.loadingPhotos).toBe(false);
    });

    it('should let the user try again after a failed Load more', () => {
      api.browsePhotos.and.returnValue(of(page([asset()], 2, 10)));
      component.ngOnInit();
      api.browsePhotos.and.returnValue(throwError(() => new Error('blip')));

      component.loadMore();

      expect(component.loadingMore).toBe(false);
    });

    it('should track thumbnails by asset so the grid does not refetch', () => {
      expect(component.trackByAssetId(0, asset({ id: 'a9' }))).toBe('a9');
      expect(component.thumbnailUrl(asset({ id: 'a9' }))).toBe('http://x/thumb/a9');
    });
  });

  // ─── Selecting ───────────────────────────────────────────────────────

  describe('selecting', () => {
    beforeEach(() => component.ngOnInit());

    it('should select and deselect', () => {
      const a = asset();

      component.toggle(a);
      expect(component.isSelected(a)).toBe(true);

      component.toggle(a);
      expect(component.isSelected(a)).toBe(false);
    });

    it('should start each pick at one 4x6', () => {
      component.toggle(asset());

      expect(component.selectedList[0].sizeCode).toBe('4x6');
      expect(component.selectedList[0].quantity).toBe(1);
    });

    it('should keep selections across a filter change', () => {
      // Picking a few from last week and a few from last year is the normal
      // way to use this page; losing the first lot would make it unusable.
      component.toggle(asset({ id: 'a1' }));

      component.dateRange = 'year';
      component.onFiltersChanged();

      expect(component.selectedList.length).toBe(1);
    });

    it('should count prints, not rows', () => {
      // The per-run ceiling is on prints, and so is the price.
      component.toggle(asset({ id: 'a1' }));
      component.toggle(asset({ id: 'a2' }));
      component.setQuantity(component.selectedList[0], 5);

      expect(component.selectedList.length).toBe(2);
      expect(component.totalPrints).toBe(6);
    });

    it('should flag going over the run ceiling', () => {
      api.getStatus.and.returnValue(of(status({ maxPrintsPerRun: 10 })));
      component.ngOnInit();
      component.toggle(asset());

      component.setQuantity(component.selectedList[0], 11);
      expect(component.overLimit).toBe(true);

      component.setQuantity(component.selectedList[0], 10);
      expect(component.overLimit).toBe(false);
    });

    it('should hold quantity between one and ninety-nine', () => {
      component.toggle(asset());
      const selection = component.selectedList[0];

      component.setQuantity(selection, 0);
      expect(selection.quantity).toBe(1);

      component.setQuantity(selection, 500);
      expect(selection.quantity).toBe(99);

      component.setQuantity(selection, '3.6');
      expect(selection.quantity).toBe(4);

      component.setQuantity(selection, 'not a number');
      expect(selection.quantity).toBe(1);
    });

    it('should remove one and clear all', () => {
      component.toggle(asset({ id: 'a1' }));
      component.toggle(asset({ id: 'a2' }));

      component.remove(component.selectedList[0]);
      expect(component.selectedList.length).toBe(1);

      component.clearAll();
      expect(component.selectedList).toEqual([]);
    });

    it('should drop a stale order result on any change to the basket', () => {
      // The result describes the basket that produced it — leaving it up beside
      // a changed basket would claim prints that were never prepared.
      const result = { runId: 'r', prepared: 1, failed: 0, belowQualityFloor: 0 };
      const a = asset();

      component.result = result;
      component.toggle(a);
      expect(component.result).toBeNull();

      component.result = result;
      component.setSize(component.selectedList[0], '8x10');
      expect(component.result).toBeNull();

      component.result = result;
      component.setQuantity(component.selectedList[0], 2);
      expect(component.result).toBeNull();

      component.result = result;
      component.clearAll();
      expect(component.result).toBeNull();
    });
  });

  // ─── The resolution warning ──────────────────────────────────────────

  describe('the resolution warning', () => {
    beforeEach(() => component.ngOnInit());

    it('should pass a big photo at a small print', () => {
      component.toggle(asset({ width: 4000, height: 3000 }));

      expect(component.looksLowResolution(component.selectedList[0])).toBe(false);
    });

    it('should flag a small photo blown up', () => {
      // 800×600 across 8×10 inches is 80 dpi — visibly soft on paper.
      component.toggle(asset({ width: 800, height: 600 }));
      component.setSize(component.selectedList[0], '8x10');

      expect(component.looksLowResolution(component.selectedList[0])).toBe(true);
    });

    it('should orient the print to the photo', () => {
      // A portrait photo prints 8 wide by 10 tall; a landscape one 10 by 8.
      //
      // Both of these clear 150 dpi when measured the right way round and fall
      // to 120 dpi when measured the other way, so a component that ignored
      // orientation would flag both of them. Passing is the assertion.
      component.toggle(asset({ id: 'portrait', width: 1200, height: 2000 }));
      component.toggle(asset({ id: 'landscape', width: 2000, height: 1200 }));
      component.setSize(component.selectedList[0], '8x10');
      component.setSize(component.selectedList[1], '8x10');

      expect(component.looksLowResolution(component.selectedList[0])).toBe(false);
      expect(component.looksLowResolution(component.selectedList[1])).toBe(false);
    });

    it('should use one edge for a square print', () => {
      component.toggle(asset({ width: 2400, height: 2400 }));
      component.setSize(component.selectedList[0], '8x8');

      expect(component.looksLowResolution(component.selectedList[0])).toBe(false);
    });

    it('should say nothing when it cannot tell', () => {
      // No dimensions from Immich, or a size code that is not in the list.
      component.toggle(asset({ width: 0, height: 0 }));
      expect(component.looksLowResolution(component.selectedList[0])).toBe(false);

      component.setSize(component.selectedList[0], 'nonexistent');
      expect(component.looksLowResolution(component.selectedList[0])).toBe(false);
    });
  });

  // ─── Placing the order ───────────────────────────────────────────────

  describe('placing the order', () => {
    beforeEach(() => {
      component.ngOnInit();
      component.toggle(asset({ id: 'a1', fileName: 'one.jpg' }));
      component.toggle(asset({ id: 'a2', fileName: 'two.jpg' }));
    });

    it('should create a run, add every item, then prepare', () => {
      component.prepareOrder();

      expect(api.createRun).toHaveBeenCalled();
      expect(api.addItem).toHaveBeenCalledTimes(2);
      expect(api.prepare).toHaveBeenCalledWith('run-1');
      expect(component.result?.prepared).toBe(1);
      expect(component.preparing).toBe(false);
    });

    it('should send each item with its own size and quantity', () => {
      component.setSize(component.selectedList[1], '8x10');
      component.setQuantity(component.selectedList[1], 3);

      component.prepareOrder();

      expect(api.addItem).toHaveBeenCalledWith('run-1', 'a2', 'two.jpg', '8x10', 3);
    });

    it('should do nothing with an empty basket', () => {
      component.clearAll();

      component.prepareOrder();

      expect(api.createRun).not.toHaveBeenCalled();
    });

    it('should not start a second order over the first', () => {
      api.createRun.and.returnValue(new Subject<{ runId: string }>().asObservable());

      component.prepareOrder();
      component.prepareOrder();

      expect(api.createRun).toHaveBeenCalledTimes(1);
      expect(component.preparing).toBe(true);
    });

    it('should stop at the item that was rejected', () => {
      // Items go one at a time precisely so a rejection names the photo that
      // caused it rather than failing the whole batch anonymously.
      api.addItem.and.returnValue(throwError(() => ({ error: { error: 'Over the limit' } })));

      component.prepareOrder();

      expect(api.addItem).toHaveBeenCalledTimes(1);
      expect(api.prepare).not.toHaveBeenCalled();
      expect(component.error).toBe('Over the limit');
      expect(component.preparing).toBe(false);
    });

    it('should name the photo when the server gives no reason', () => {
      api.addItem.and.returnValue(throwError(() => new Error('500')));

      component.prepareOrder();

      expect(component.error).toContain('one.jpg');
    });

    it('should report a failure to start the run', () => {
      api.createRun.and.returnValue(throwError(() => new Error('down')));

      component.prepareOrder();

      expect(component.error).toBe('Could not start the order.');
      expect(component.preparing).toBe(false);
    });

    it('should surface the server\'s reason for refusing to prepare', () => {
      api.prepare.and.returnValue(throwError(() => ({ error: { error: 'Nothing above the quality floor' } })));

      component.prepareOrder();

      expect(component.error).toBe('Nothing above the quality floor');
      expect(component.preparing).toBe(false);
    });
  });
});
