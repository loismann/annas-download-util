import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';

import { BulkImportMoviesDialogComponent } from './bulk-import-movies-dialog.component';
import { BulkImportMovieResult, MediaSearchApiService } from '../../services/media-search-api.service';

/**
 * Characterization tests for the bulk movie import.
 *
 * A review pass in the same series as the library grids. It found two things:
 * a year validator that let `197a` through as the year 197, and a failure
 * message written to a field the stage it returns to never renders.
 *
 * The CSV parser gets the most attention here because it is the only piece of
 * this dialog with no server-side second opinion — a row it mangles is a row
 * Radarr is asked about wrongly.
 */
describe('BulkImportMoviesDialogComponent (characterization)', () => {
  let fixture: ComponentFixture<BulkImportMoviesDialogComponent>;
  let component: BulkImportMoviesDialogComponent;
  let api: jasmine.SpyObj<MediaSearchApiService>;
  let dialogRef: jasmine.SpyObj<MatDialogRef<BulkImportMoviesDialogComponent>>;

  /**
   * Runs a CSV through the real file-input path and waits for the read.
   *
   * `file.text()` resolves on the browser's own schedule — often more than one
   * tick — so this waits for the component to have reacted rather than
   * guessing at a number of ticks. Every path out of the read sets one of the
   * two, so there is no case where this spins the full count.
   */
  async function upload(csv: string): Promise<void> {
    const file = new File([csv], 'movies.csv', { type: 'text/csv' });
    component.onFileSelected({ target: { files: [file] } } as unknown as Event);

    for (let i = 0; i < 100 && component.stage === 'upload' && !component.parseError; i++) {
      await new Promise(resolve => setTimeout(resolve, 1));
    }
  }

  function result(over: Partial<BulkImportMovieResult> = {}): BulkImportMovieResult {
    return { title: 'A Movie', status: 'added', ...over };
  }

  beforeEach(async () => {
    api = jasmine.createSpyObj<MediaSearchApiService>('MediaSearchApiService', ['bulkImportMovies']);
    api.bulkImportMovies.and.returnValue(of([]));
    dialogRef = jasmine.createSpyObj<MatDialogRef<BulkImportMoviesDialogComponent>>('MatDialogRef', ['close']);

    await TestBed.configureTestingModule({
      imports: [BulkImportMoviesDialogComponent, NoopAnimationsModule],
      providers: [
        { provide: MediaSearchApiService, useValue: api },
        { provide: MatDialogRef, useValue: dialogRef }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(BulkImportMoviesDialogComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => fixture.destroy());

  // ─── Parsing ─────────────────────────────────────────────────────────

  describe('parsing', () => {
    it('should read title, year, owner and every genre column after them', async () => {
      await upload('The Street Fighter,1974,Paul,Date Night,Kung Fu');

      expect(component.rows).toEqual([{
        title: 'The Street Fighter', year: 1974, owner: 'Paul',
        genres: ['Date Night', 'Kung Fu'], error: null
      }]);
    });

    it('should skip a header row', async () => {
      await upload('title,year,owner,genre\nThem!,1954,Mom,Sci-Fi');

      expect(component.rows.length).toBe(1);
      expect(component.rows[0].title).toBe('Them!');
    });

    it('should treat the first row as data when there is no header', async () => {
      await upload('Them!,1954\nThe Blob,1958');

      expect(component.rows.map(r => r.title)).toEqual(['Them!', 'The Blob']);
    });

    it('should honour quoted fields containing commas and quotes', async () => {
      // The reason this parser exists rather than a split(',').
      await upload('"Hello, Dolly!",1969,Mom,"He said ""hi"""');

      expect(component.rows[0].title).toBe('Hello, Dolly!');
      expect(component.rows[0].genres).toEqual(['He said "hi"']);
    });

    it('should accept CRLF as well as LF', async () => {
      await upload('Them!,1954\r\nThe Blob,1958\r\n');

      expect(component.rows.length).toBe(2);
    });

    it('should ignore blank lines', async () => {
      await upload('Them!,1954\n\n\nThe Blob,1958\n');

      expect(component.rows.map(r => r.title)).toEqual(['Them!', 'The Blob']);
    });

    it('should take a row with no year at all', async () => {
      // Title alone is enough; the backend can match on it.
      await upload('Them!');

      expect(component.rows[0].error).toBeNull();
      expect(component.rows[0].year).toBeNull();
    });

    it('should say so when the file has no rows', async () => {
      await upload('\n\n');

      expect(component.parseError).toBe('No rows found in that file.');
      expect(component.stage).toBe('upload');
    });

    it('should do nothing when no file was chosen', () => {
      component.onFileSelected({ target: { files: [] } } as unknown as Event);

      expect(component.stage).toBe('upload');
    });
  });

  // ─── Row validation ──────────────────────────────────────────────────

  describe('row validation', () => {
    it('should require a title', async () => {
      await upload(',1954,Paul');

      expect(component.rows[0].error).toBe('Title is required');
    });

    /**
     * The first of the two defects this pass found.
     *
     * The check was `Number.isNaN(parseInt(raw)) || raw.length !== 4`. parseInt
     * stops at the first non-digit, so `197a` parsed to 197 and passed the
     * length check — the row was submitted with the year 197. The backend
     * matches on title+year, so it would simply find nothing and report the
     * movie missing, with the actual cause four columns away in a CSV.
     */
    it('should reject a year that is not four digits', async () => {
      await upload('Them!,197a\nThe Blob,54\nForbidden Planet,19544\nIt,1956');

      expect(component.rows.map(r => r.error)).toEqual([
        'Invalid year "197a"',
        'Invalid year "54"',
        'Invalid year "19544"',
        null
      ]);
    });

    it('should only accept a household owner', async () => {
      await upload('Them!,1954,Nobody');

      expect(component.rows[0].error).toContain('Owner must be one of');
    });

    it('should not care how the owner is capitalised', async () => {
      await upload('Them!,1954,mom');

      expect(component.rows[0].error).toBeNull();
      expect(component.rows[0].owner).toBe('mom');
    });

    it('should count what is importable and what is not', async () => {
      await upload('Them!,1954\n,1958\nIt,19x4');

      expect(component.validCount).toBe(1);
      expect(component.invalidCount).toBe(2);
    });
  });

  // ─── Importing ───────────────────────────────────────────────────────

  describe('importing', () => {
    /** 25 valid rows — one full chunk of 20 plus a short one. */
    const twentyFive = Array.from({ length: 25 }, (_, i) => `Movie ${i},195${i % 10}`).join('\n');

    it('should submit only the rows that are valid, without their error field', async () => {
      await upload('Them!,1954,Paul,Sci-Fi\n,1958');

      component.onImport();

      expect(api.bulkImportMovies).toHaveBeenCalledWith(
        [{ title: 'Them!', year: 1954, owner: 'Paul', genres: ['Sci-Fi'] }], false);
    });

    it('should submit in chunks rather than one long request', async () => {
      // Each row is a TMDB-backed lookup plus an add, so a few hundred rows is
      // minutes of server work — past what one HTTP request survives.
      await upload(twentyFive);
      api.bulkImportMovies.and.returnValues(
        of(Array.from({ length: 20 }, () => result())),
        of(Array.from({ length: 5 }, () => result()))
      );

      component.onImport();

      expect(api.bulkImportMovies).toHaveBeenCalledTimes(2);
      expect(api.bulkImportMovies.calls.first().args[0].length).toBe(20);
      expect(api.bulkImportMovies.calls.mostRecent().args[0].length).toBe(5);
      expect(component.stage).toBe('results');
      expect(component.results.length).toBe(25);
    });

    it('should pass the Date Night pool flag through', async () => {
      // Pool movies are catalog records only — nothing downloads until a date
      // night is scheduled, so this must not silently default to acquiring.
      await upload('Them!,1954');
      component.dateNightPool = true;

      component.onImport();

      expect(api.bulkImportMovies).toHaveBeenCalledWith(jasmine.any(Array), true);
    });

    it('should report progress against what it set out to do', async () => {
      await upload('Them!,1954\n,1958');

      component.onImport();

      expect(component.submittedCount).toBe(1);
    });

    it('should keep the rows that already landed when a later chunk fails', async () => {
      // Re-running the same file is safe — rows that landed come back as
      // "already in Radarr" — so showing partial results beats discarding them.
      await upload(twentyFive);
      api.bulkImportMovies.and.returnValues(
        of(Array.from({ length: 20 }, () => result())),
        throwError(() => new Error('gateway timeout'))
      );

      component.onImport();

      expect(component.stage).toBe('results');
      expect(component.results.length).toBe(20);
    });

    /**
     * The second defect this pass found.
     *
     * When the very first chunk failed there was nothing to show, so the dialog
     * went back to the preview stage and set `parseError` to explain why. But
     * `parseError` was only rendered on the *upload* stage — the preview stage
     * had no element bound to it. The user was returned to a screen that looked
     * exactly as it had before they pressed Import, with no indication that
     * anything had happened at all.
     */
    it('should show why an import that added nothing failed', async () => {
      await upload('Them!,1954');
      api.bulkImportMovies.and.returnValue(throwError(() => new Error('down')));

      component.onImport();
      fixture.detectChanges();

      expect(component.stage).toBe('preview');
      expect(component.parseError).toBe('Import failed — please try again.');
      const shown = fixture.nativeElement.querySelector('.preview-stage .error');
      expect(shown?.textContent).toContain('Import failed');
    });

    it('should tally the outcomes', async () => {
      await upload('Them!,1954');
      api.bulkImportMovies.and.returnValue(of([
        result({ status: 'added' }),
        result({ status: 'added' }),
        result({ status: 'already-existed' }),
        result({ status: 'not-found' }),
        result({ status: 'ambiguous' })
      ]));

      component.onImport();

      expect(component.addedCount).toBe(2);
      expect(component.existedCount).toBe(1);
      // Everything that is neither added nor already there is a skip.
      expect(component.failedCount).toBe(2);
    });

    it('should start a second import from an empty tally', async () => {
      await upload('Them!,1954');
      api.bulkImportMovies.and.returnValue(of([result()]));
      component.onImport();
      expect(component.results.length).toBe(1);

      component.stage = 'preview';
      component.onImport();

      expect(component.results.length).toBe(1);
    });
  });

  // ─── Closing ─────────────────────────────────────────────────────────

  describe('closing', () => {
    it('should tell the caller nothing changed when cancelled', () => {
      component.onClose();

      expect(dialogRef.close).toHaveBeenCalledWith(false);
    });

    it('should tell the caller to refresh after a run', async () => {
      await upload('Them!,1954');
      component.onImport();

      component.onClose();

      expect(dialogRef.close).toHaveBeenCalledWith(true);
    });
  });
});
