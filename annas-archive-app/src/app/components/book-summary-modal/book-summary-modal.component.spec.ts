import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';

import { BookSummaryModalComponent, BookSummaryModalData } from './book-summary-modal.component';
import { BookDto } from '../../models/book-dto.model';

/**
 * Characterization tests for the full-summary popup.
 *
 * Read-only, so there is one piece of behaviour: which cover it shows. Search
 * results carry a list of candidate cover URLs of varying reliability, and this
 * takes the first — the same one the card behind it is showing, so opening the
 * popup does not appear to change the book.
 */
describe('BookSummaryModalComponent (characterization)', () => {
  let fixture: ComponentFixture<BookSummaryModalComponent>;
  let component: BookSummaryModalComponent;
  let dialogRef: jasmine.SpyObj<MatDialogRef<BookSummaryModalComponent>>;

  function book(over: Partial<BookDto> = {}): BookDto {
    return { title: 'Dune', author: 'Frank Herbert', md5: 'abc', ...over } as BookDto;
  }

  async function build(over: Partial<BookSummaryModalData> = {}): Promise<void> {
    dialogRef = jasmine.createSpyObj<MatDialogRef<BookSummaryModalComponent>>('MatDialogRef', ['close']);

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [BookSummaryModalComponent, NoopAnimationsModule],
      providers: [
        { provide: MatDialogRef, useValue: dialogRef },
        {
          provide: MAT_DIALOG_DATA,
          useValue: { book: book(), placeholderUrl: '/assets/placeholder.jpg', ...over } as BookSummaryModalData
        }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(BookSummaryModalComponent);
    component = fixture.componentInstance;
  }

  beforeEach(async () => build());

  afterEach(() => fixture.destroy());

  it('should show the first candidate cover', async () => {
    // The same one the card behind it shows, so opening this does not appear to
    // change which book is being looked at.
    await build({ book: book({ coverCandidates: ['http://x/a.jpg', 'http://x/b.jpg'] }) });

    expect(component.coverUrl).toBe('http://x/a.jpg');
  });

  it('should fall back to the placeholder with no candidates', async () => {
    await build({ book: book({ coverCandidates: [] }) });

    expect(component.coverUrl).toBe('/assets/placeholder.jpg');
  });

  it('should fall back when the field is missing entirely', () => {
    expect(component.coverUrl).toBe('/assets/placeholder.jpg');
  });

  it('should take the placeholder from the caller', async () => {
    // The search grid and the library use different ones.
    await build({ placeholderUrl: '/assets/other.jpg' });

    expect(component.coverUrl).toBe('/assets/other.jpg');
  });

  it('should show the book it was given', () => {
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Dune');
  });

  it('should close on the close button', () => {
    component.close();

    expect(dialogRef.close).toHaveBeenCalled();
  });
});
