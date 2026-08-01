import { ComponentFixture, TestBed } from '@angular/core/testing';
import { SearchResultsComponent, DisplayGroup } from './search-results.component';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { BookDto } from '../../models/book-dto.model';
import { BookGroup } from '../../models/book-group.model';

describe('SearchResultsComponent', () => {
  let component: SearchResultsComponent;
  let fixture: ComponentFixture<SearchResultsComponent>;

  const createMockBook = (overrides: Partial<BookDto> = {}): BookDto => ({
    md5: 'test-md5-123',
    title: 'Test Book Title',
    authors: ['Test Author'],
    language: 'English',
    format: 'EPUB',
    source: 'anna',
    fileSize: '1.5 MB',
    bookType: 'Fiction',
    publisher: 'Test Publisher',
    year: 2023,
    isbn: '978-0-123456-78-9',
    coverCandidates: ['https://example.com/cover.jpg'],
    description: null,
    descriptionSource: null,
    sendState: 'idle',
    libraryState: 'idle',
    dadsKindleState: 'idle',
    momsKindleState: 'idle',
    ...overrides
  });

  /** Wraps one or more books (same underlying book, different files) into a
   *  DisplayGroup the way book-search.component.ts's displayGroups getter
   *  would — the first book is "active" unless a variant was picked. */
  const createDisplayGroup = (books: BookDto[]): DisplayGroup => {
    const group: BookGroup = { key: books[0].md5, books };
    return { group, active: books[0] };
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [SearchResultsComponent, NoopAnimationsModule]
    }).compileComponents();

    fixture = TestBed.createComponent(SearchResultsComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  describe('Inputs', () => {
    it('should display one tile per group', () => {
      component.groups = [
        createDisplayGroup([createMockBook()]),
        createDisplayGroup([createMockBook({ md5: 'test-md5-456', title: 'Another Book' })])
      ];
      fixture.detectChanges();
      const tiles = fixture.nativeElement.querySelectorAll('.book-tile');
      expect(tiles.length).toBe(2);
    });

    it('should show no results message when searchPerformed but no groups', () => {
      component.groups = [];
      component.searchPerformed = true;
      component.loading = false;
      fixture.detectChanges();
      const noResults = fixture.nativeElement.querySelector('.results-inner p');
      expect(noResults?.textContent).toContain('No results');
    });

    it('should not show no results when loading', () => {
      component.groups = [];
      component.searchPerformed = true;
      component.loading = true;
      fixture.detectChanges();
      const noResults = fixture.nativeElement.querySelector('.results-inner p');
      expect(noResults).toBeNull();
    });

    it('should not show no results when search not performed', () => {
      component.groups = [];
      component.searchPerformed = false;
      fixture.detectChanges();
      // Assert on the text, not on "is there any <p>" — before a search the
      // pre-search hint legitimately renders its own paragraphs, so a bare
      // `.results-inner p` selector matches those and reads as a failure.
      const paragraphs = Array.from(
        fixture.nativeElement.querySelectorAll('.results-inner p')
      ) as HTMLElement[];
      expect(paragraphs.some(p => p.textContent?.trim() === 'No results')).toBe(false);
    });

    it('should show a grouping indicator while grouping and nothing has landed yet', () => {
      component.groups = [];
      component.groupingInProgress = true;
      fixture.detectChanges();
      const indicator = fixture.nativeElement.querySelector('.grouping-indicator');
      expect(indicator).toBeTruthy();
    });
  });

  describe('Grouping helpers', () => {
    it('formatsOf returns the distinct formats across a group', () => {
      const group: BookGroup = {
        key: 'a',
        books: [
          createMockBook({ md5: 'a', format: 'EPUB' }),
          createMockBook({ md5: 'b', format: 'PDF' }),
          createMockBook({ md5: 'c', format: 'EPUB' })
        ]
      };
      expect(component.formatsOf(group).sort()).toEqual(['EPUB', 'PDF']);
    });

    it('sameFormatSiblings returns only books sharing the active format', () => {
      const epub1 = createMockBook({ md5: 'a', format: 'EPUB' });
      const epub2 = createMockBook({ md5: 'b', format: 'EPUB' });
      const pdf = createMockBook({ md5: 'c', format: 'PDF' });
      const group: BookGroup = { key: 'a', books: [epub1, epub2, pdf] };
      expect(component.sameFormatSiblings(group, epub1)).toEqual([epub1, epub2]);
    });

    it('onPickFormat emits variantSelected for a book matching the requested format', () => {
      spyOn(component.variantSelected, 'emit');
      const epub = createMockBook({ md5: 'a', format: 'EPUB' });
      const pdf = createMockBook({ md5: 'b', format: 'PDF' });
      const dg = createDisplayGroup([epub, pdf]);

      component.onPickFormat(dg, 'PDF');

      expect(component.variantSelected.emit).toHaveBeenCalledWith({ group: dg.group, book: pdf });
    });

    it('onPickFormat does nothing when the requested format is already active', () => {
      spyOn(component.variantSelected, 'emit');
      const dg = createDisplayGroup([createMockBook({ format: 'EPUB' })]);

      component.onPickFormat(dg, 'EPUB');

      expect(component.variantSelected.emit).not.toHaveBeenCalled();
    });
  });

  describe('Cover URL', () => {
    it('should return first cover candidate if available', () => {
      const book = createMockBook({ coverCandidates: ['https://example.com/cover1.jpg', 'https://example.com/cover2.jpg'] });
      expect(component.getCoverUrl(book)).toBe('https://example.com/cover1.jpg');
    });

    it('should return placeholder if no cover candidates', () => {
      const book = createMockBook({ coverCandidates: [] });
      component.placeholderUrl = '/assets/placeholder.jpg';
      expect(component.getCoverUrl(book)).toBe('/assets/placeholder.jpg');
    });
  });

  describe('Outputs', () => {
    it('should emit sendToLibrary event', () => {
      spyOn(component.sendToLibrary, 'emit');
      const book = createMockBook();
      component.onSendToLibrary(book);
      expect(component.sendToLibrary.emit).toHaveBeenCalledWith({ book });
    });

    it('should emit sendToDropbox event', () => {
      spyOn(component.sendToDropbox, 'emit');
      const book = createMockBook();
      component.onSendToDropbox(book);
      expect(component.sendToDropbox.emit).toHaveBeenCalledWith({ book });
    });

    it('should emit sendToKindle event for dad', () => {
      spyOn(component.sendToKindle, 'emit');
      const book = createMockBook();
      component.onSendToDadsKindle(book);
      expect(component.sendToKindle.emit).toHaveBeenCalledWith({ book, target: 'dad' });
    });

    it('should emit sendToKindle event for mom', () => {
      spyOn(component.sendToKindle, 'emit');
      const book = createMockBook();
      component.onSendToMomsKindle(book);
      expect(component.sendToKindle.emit).toHaveBeenCalledWith({ book, target: 'mom' });
    });

    it('should emit fetchDescription event', () => {
      spyOn(component.fetchDescription, 'emit');
      const book = createMockBook();
      component.onFetchDescription(book);
      expect(component.fetchDescription.emit).toHaveBeenCalledWith({ book });
    });

    it('should emit coverError event', () => {
      spyOn(component.coverError, 'emit');
      const book = createMockBook();
      const event = new Event('error');
      component.onCoverError(book, event);
      expect(component.coverError.emit).toHaveBeenCalledWith({ book, event });
    });

    it('should emit openSummary when a tile is clicked', () => {
      spyOn(component.openSummary, 'emit');
      const book = createMockBook();
      component.onTileClick(book);
      expect(component.openSummary.emit).toHaveBeenCalledWith(book);
    });
  });

  describe('Button states', () => {
    it('should display sending state for library button', () => {
      component.groups = [createDisplayGroup([createMockBook({ libraryState: 'sending' })])];
      fixture.detectChanges();
      const button = fixture.nativeElement.querySelector('.tile-actions button:first-child');
      expect(button.textContent).toContain('Saving');
    });

    it('should display success state for library button', () => {
      component.groups = [createDisplayGroup([createMockBook({ libraryState: 'success' })])];
      fixture.detectChanges();
      const button = fixture.nativeElement.querySelector('.tile-actions button:first-child');
      expect(button.textContent).toContain('Saved');
    });

    it('should disable Kindle buttons for non-EPUB books', () => {
      component.groups = [createDisplayGroup([createMockBook({ format: 'PDF' })])];
      fixture.detectChanges();
      const kindleButtons = fixture.nativeElement.querySelectorAll('.tile-actions button[disabled]');
      // Dad's Kindle and Mom's Kindle should be disabled
      expect(kindleButtons.length).toBeGreaterThanOrEqual(2);
    });

    it('should enable Kindle buttons for EPUB books', () => {
      component.groups = [createDisplayGroup([createMockBook({ format: 'EPUB' })])];
      fixture.detectChanges();
      const buttons = fixture.nativeElement.querySelectorAll('.tile-actions button');
      // Third and fourth buttons are Kindle buttons
      expect(buttons[2].disabled).toBe(false);
      expect(buttons[3].disabled).toBe(false);
    });
  });

  describe('Description display', () => {
    it('should show description when present', () => {
      component.groups = [createDisplayGroup([createMockBook({ description: 'This is a test description' })])];
      fixture.detectChanges();
      const description = fixture.nativeElement.querySelector('.tile-description');
      expect(description?.textContent).toContain('This is a test description');
    });

    it('should show GPT icon for AI-generated description', () => {
      component.groups = [createDisplayGroup([createMockBook({ description: 'AI description', descriptionSource: 'gpt' })])];
      fixture.detectChanges();
      const icon = fixture.nativeElement.querySelector('.robot-icon');
      expect(icon).toBeTruthy();
    });

    it('should show Google Books icon', () => {
      component.groups = [createDisplayGroup([createMockBook({ description: 'Google description', descriptionSource: 'googlebooks' })])];
      fixture.detectChanges();
      const icon = fixture.nativeElement.querySelector('.book-icon');
      expect(icon).toBeTruthy();
    });

    it('should show OpenLibrary icon', () => {
      component.groups = [createDisplayGroup([createMockBook({ description: 'OpenLibrary description', descriptionSource: 'openlibrary' })])];
      fixture.detectChanges();
      const icon = fixture.nativeElement.querySelector('.leaf-icon');
      expect(icon).toBeTruthy();
    });

    it('should show a retrieve-summary button whenever a book has no description yet', () => {
      component.groups = [createDisplayGroup([createMockBook({ description: null })])];
      fixture.detectChanges();
      const retrieveButton = fixture.nativeElement.querySelector('.retrieve-summary-btn');
      expect(retrieveButton).toBeTruthy();
    });

    it('should not show a retrieve-summary button once a description exists', () => {
      component.groups = [createDisplayGroup([createMockBook({ description: 'Already have one' })])];
      fixture.detectChanges();
      const retrieveButton = fixture.nativeElement.querySelector('.retrieve-summary-btn');
      expect(retrieveButton).toBeFalsy();
    });
  });
});
