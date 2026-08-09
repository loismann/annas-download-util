import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { Subject, of, throwError } from 'rxjs';

import { CoverPickerComponent } from './cover-picker.component';
import { CoverCandidate, CoverCandidates } from './cover-candidates';
import { LibraryApiService } from '../../../services/library-api.service';

/**
 * Characterization tests for the shared cover picker.
 *
 * `cover-candidates.spec.ts` already covers the probing and sorting; this
 * covers the component wrapped around it — when it fetches, what it says when
 * it finds nothing, and the manual-URL escape hatch that exists because cover
 * lookup fails often enough to need one.
 *
 * The probe is stubbed throughout: the real one loads every URL in a browser,
 * so leaving it in would make these tests depend on the network.
 */
describe('CoverPickerComponent (characterization)', () => {
  let fixture: ComponentFixture<CoverPickerComponent>;
  let component: CoverPickerComponent;
  let api: jasmine.SpyObj<LibraryApiService>;
  let resolve: jasmine.Spy;

  function candidate(url: string, width = 400, height = 600): CoverCandidate {
    return { url, width, height, ratio: height / width };
  }

  /** Lets the async probe step inside loadCandidates settle. */
  async function probed(): Promise<void> {
    for (let i = 0; i < 50 && component.candidatesLoading; i++) {
      await new Promise(r => setTimeout(r, 1));
    }
  }

  beforeEach(async () => {
    api = jasmine.createSpyObj<LibraryApiService>('LibraryApiService', ['fetchLibraryCoverCandidates']);
    api.fetchLibraryCoverCandidates.and.returnValue(of({ covers: [] } as any));
    resolve = spyOn(CoverCandidates, 'resolve').and.resolveTo([]);

    await TestBed.configureTestingModule({
      imports: [CoverPickerComponent, NoopAnimationsModule],
      providers: [{ provide: LibraryApiService, useValue: api }]
    }).compileComponents();

    fixture = TestBed.createComponent(CoverPickerComponent);
    component = fixture.componentInstance;
    component.title = 'Dune';
  });

  afterEach(() => fixture.destroy());

  // ─── What is on show ─────────────────────────────────────────────────

  describe('the preview', () => {
    it('should prefer a new pick over the current cover over the placeholder', () => {
      expect(component.displayCoverUrl).toBe('/assets/placeholder.jpg');

      component.currentCoverUrl = 'http://x/current.jpg';
      expect(component.displayCoverUrl).toBe('http://x/current.jpg');

      component.selectedCoverUrl = 'http://x/new.jpg';
      expect(component.displayCoverUrl).toBe('http://x/new.jpg');
    });

    it('should fall back to the placeholder when an image will not load', () => {
      const img = document.createElement('img');
      img.src = 'http://x/broken.jpg';

      component.onCoverError({ target: img } as unknown as Event);

      expect(img.src).toContain('/assets/placeholder.jpg');
    });

    it('should not loop when the placeholder itself is what failed', () => {
      // Reassigning the same src would fire error again, forever.
      const img = document.createElement('img');
      img.src = `${location.origin}/assets/placeholder.jpg`;
      const before = img.src;

      component.onCoverError({ target: img } as unknown as Event);

      expect(img.src).toBe(before);
    });
  });

  // ─── Opening ─────────────────────────────────────────────────────────

  describe('opening the picker', () => {
    it('should stay closed until asked', () => {
      component.ngOnInit();

      expect(component.pickerOpen).toBe(false);
      expect(api.fetchLibraryCoverCandidates).not.toHaveBeenCalled();
    });

    it('should open and search straight away when told to', () => {
      // The book edit dialog mounts this already open — waiting for a click on
      // a thumbnail the user has just chosen to replace would be a step too many.
      component.autoOpen = true;

      component.ngOnInit();

      expect(component.pickerOpen).toBe(true);
      expect(api.fetchLibraryCoverCandidates).toHaveBeenCalled();
    });

    it('should search on first open only', () => {
      component.togglePicker();
      component.togglePicker();
      component.togglePicker();

      expect(api.fetchLibraryCoverCandidates).toHaveBeenCalledTimes(1);
    });

    it('should search again if the first attempt found nothing', async () => {
      // Otherwise a transient failure leaves the panel permanently empty.
      component.togglePicker();
      await probed();
      component.togglePicker();
      component.togglePicker();

      expect(api.fetchLibraryCoverCandidates).toHaveBeenCalledTimes(2);
    });

    it('should not search again once it has results', async () => {
      resolve.and.resolveTo([candidate('http://x/a.jpg')]);
      component.togglePicker();
      await probed();

      component.togglePicker();
      component.togglePicker();

      expect(api.fetchLibraryCoverCandidates).toHaveBeenCalledTimes(1);
    });
  });

  // ─── Searching ───────────────────────────────────────────────────────

  describe('searching', () => {
    it('should send the title and author', () => {
      component.author = 'Frank Herbert';

      component.togglePicker();

      expect(api.fetchLibraryCoverCandidates).toHaveBeenCalledWith('Dune', 'Frank Herbert');
    });

    it('should send no author when there is none', () => {
      component.author = null;

      component.togglePicker();

      expect(api.fetchLibraryCoverCandidates).toHaveBeenCalledWith('Dune', undefined);
    });

    it('should refuse to search with no title', () => {
      component.title = '   ';

      component.togglePicker();

      expect(api.fetchLibraryCoverCandidates).not.toHaveBeenCalled();
      expect(component.candidatesError).toContain('Missing title');
    });

    it('should probe each candidate once even when the sources overlap', async () => {
      // The same cover arriving twice would be loaded twice and shown twice.
      api.fetchLibraryCoverCandidates.and.returnValue(
        of({ covers: ['http://x/a.jpg', 'http://x/b.jpg', 'http://x/a.jpg'] } as any));

      component.togglePicker();
      await probed();

      expect(resolve).toHaveBeenCalledWith(['http://x/a.jpg', 'http://x/b.jpg']);
    });

    it('should keep whatever survived probing', async () => {
      resolve.and.resolveTo([candidate('http://x/big.jpg', 800, 1200)]);

      component.togglePicker();
      await probed();

      expect(component.candidates.length).toBe(1);
      expect(component.candidatesError).toBeNull();
      expect(component.candidatesLoading).toBe(false);
    });

    it('should point at the escape hatches when nothing is found', async () => {
      // A dead end with no next step is the worst outcome here, so the message
      // names the two ways out.
      component.togglePicker();
      await probed();

      expect(component.candidatesError).toContain('Google Images');
      expect(component.candidatesError).toContain('manually');
    });

    it('should point at them when the lookup itself fails too', () => {
      api.fetchLibraryCoverCandidates.and.returnValue(throwError(() => new Error('down')));

      component.togglePicker();

      expect(component.candidatesError).toContain('Google Images');
      expect(component.candidatesLoading).toBe(false);
    });

    it('should stop the spinner when probing throws', async () => {
      resolve.and.rejectWith(new Error('probe blew up'));

      component.togglePicker();
      await probed();

      expect(component.candidatesLoading).toBe(false);
      expect(component.candidatesError).toContain('Failed to load');
    });

    it('should clear the old results before refreshing', async () => {
      resolve.and.resolveTo([candidate('http://x/a.jpg')]);
      component.togglePicker();
      await probed();

      const pending = new Subject<any>();
      api.fetchLibraryCoverCandidates.and.returnValue(pending.asObservable());
      component.refreshCandidates();

      expect(component.candidates).toEqual([]);
      expect(component.candidatesLoading).toBe(true);
    });

    it('should not apply a lookup that lands after destroy', () => {
      const late = new Subject<any>();
      api.fetchLibraryCoverCandidates.and.returnValue(late.asObservable());
      component.togglePicker();

      fixture.destroy();
      late.next({ covers: ['http://x/a.jpg'] });

      expect(resolve).not.toHaveBeenCalled();
    });
  });

  // ─── Choosing ────────────────────────────────────────────────────────

  describe('choosing a cover', () => {
    it('should report the choice and show it', () => {
      const chosen = jasmine.createSpy('coverSelected');
      component.coverSelected.subscribe(chosen);

      component.selectCover('http://x/a.jpg');

      expect(chosen).toHaveBeenCalledWith('http://x/a.jpg');
      expect(component.displayCoverUrl).toBe('http://x/a.jpg');
    });

    it('should take a pasted URL and clear the box', () => {
      const chosen = jasmine.createSpy('coverSelected');
      component.coverSelected.subscribe(chosen);
      component.manualCoverUrl = '  http://x/manual.jpg  ';

      component.applyManualCoverUrl();

      expect(chosen).toHaveBeenCalledWith('http://x/manual.jpg');
      expect(component.manualCoverUrl).toBe('');
    });

    it('should ignore an empty paste', () => {
      const chosen = jasmine.createSpy('coverSelected');
      component.coverSelected.subscribe(chosen);
      component.manualCoverUrl = '   ';

      component.applyManualCoverUrl();

      expect(chosen).not.toHaveBeenCalled();
    });
  });

  // ─── The Google Images escape hatch ──────────────────────────────────

  describe('the Google Images link', () => {
    it('should search for the title, the author and the word cover', () => {
      const open = spyOn(window, 'open');
      component.author = 'Frank Herbert';

      component.openGoogleImages();

      const url = open.calls.mostRecent().args[0] as string;
      expect(decodeURIComponent(url)).toContain('Dune Frank Herbert cover');
      // tbm=isch is the image tab; without it this lands on web results.
      expect(url).toContain('tbm=isch');
    });

    it('should manage without an author', () => {
      const open = spyOn(window, 'open');
      component.author = null;

      component.openGoogleImages();

      expect(decodeURIComponent(open.calls.mostRecent().args[0] as string)).toContain('Dune cover');
    });

    it('should open in a new tab so the app is not navigated away from', () => {
      const open = spyOn(window, 'open');

      component.openGoogleImages();

      expect(open.calls.mostRecent().args[1]).toBe('_blank');
    });
  });
});
