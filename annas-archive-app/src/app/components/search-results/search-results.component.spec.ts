import { ComponentFixture, TestBed } from '@angular/core/testing';
import { SearchResultsComponent } from './search-results.component';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { BookDto } from '../../models/book-dto.model';

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
    it('should display books when provided', () => {
      component.books = [createMockBook(), createMockBook({ md5: 'test-md5-456', title: 'Another Book' })];
      fixture.detectChanges();
      const cards = fixture.nativeElement.querySelectorAll('.book-card');
      expect(cards.length).toBe(2);
    });

    it('should show no results message when searchPerformed but no books', () => {
      component.books = [];
      component.searchPerformed = true;
      component.loading = false;
      fixture.detectChanges();
      const noResults = fixture.nativeElement.querySelector('.results-inner p');
      expect(noResults?.textContent).toContain('No results');
    });

    it('should not show no results when loading', () => {
      component.books = [];
      component.searchPerformed = true;
      component.loading = true;
      fixture.detectChanges();
      const noResults = fixture.nativeElement.querySelector('.results-inner p');
      expect(noResults).toBeNull();
    });

    it('should not show no results when search not performed', () => {
      component.books = [];
      component.searchPerformed = false;
      fixture.detectChanges();
      const noResults = fixture.nativeElement.querySelector('.results-inner p');
      expect(noResults).toBeNull();
    });
  });

  describe('Card expansion', () => {
    it('should toggle card expansion', () => {
      const md5 = 'test-md5';
      expect(component.isCardExpanded(md5)).toBe(false);
      component.toggleCardExpansion(md5);
      expect(component.isCardExpanded(md5)).toBe(true);
      component.toggleCardExpansion(md5);
      expect(component.isCardExpanded(md5)).toBe(false);
    });

    it('should determine if description needs expansion', () => {
      const shortDescription = 'Short description';
      const longDescription = 'A'.repeat(200);

      expect(component.needsExpansion(shortDescription)).toBe(false);
      expect(component.needsExpansion(longDescription)).toBe(true);
    });

    it('should return false for empty description', () => {
      expect(component.needsExpansion('')).toBe(false);
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
  });

  describe('Button states', () => {
    it('should display sending state for library button', () => {
      component.books = [createMockBook({ libraryState: 'sending' })];
      fixture.detectChanges();
      const button = fixture.nativeElement.querySelector('.action-buttons button:first-child');
      expect(button.textContent).toContain('Saving');
    });

    it('should display success state for library button', () => {
      component.books = [createMockBook({ libraryState: 'success' })];
      fixture.detectChanges();
      const button = fixture.nativeElement.querySelector('.action-buttons button:first-child');
      expect(button.textContent).toContain('Saved');
    });

    it('should disable Kindle buttons for non-EPUB books', () => {
      component.books = [createMockBook({ format: 'PDF' })];
      fixture.detectChanges();
      const kindleButtons = fixture.nativeElement.querySelectorAll('.action-buttons button[disabled]');
      // Dad's Kindle and Mom's Kindle should be disabled
      expect(kindleButtons.length).toBeGreaterThanOrEqual(2);
    });

    it('should enable Kindle buttons for EPUB books', () => {
      component.books = [createMockBook({ format: 'EPUB' })];
      fixture.detectChanges();
      const buttons = fixture.nativeElement.querySelectorAll('.action-buttons button');
      // Third and fourth buttons are Kindle buttons
      expect(buttons[2].disabled).toBe(false);
      expect(buttons[3].disabled).toBe(false);
    });
  });

  describe('Description display', () => {
    it('should show description when present', () => {
      component.books = [createMockBook({ description: 'This is a test description' })];
      fixture.detectChanges();
      const description = fixture.nativeElement.querySelector('.book-description .description-text');
      expect(description?.textContent).toContain('This is a test description');
    });

    it('should show GPT icon for AI-generated description', () => {
      component.books = [createMockBook({ description: 'AI description', descriptionSource: 'gpt' })];
      fixture.detectChanges();
      const icon = fixture.nativeElement.querySelector('.robot-icon');
      expect(icon).toBeTruthy();
    });

    it('should show Google Books icon', () => {
      component.books = [createMockBook({ description: 'Google description', descriptionSource: 'googlebooks' })];
      fixture.detectChanges();
      const icon = fixture.nativeElement.querySelector('.book-icon');
      expect(icon).toBeTruthy();
    });

    it('should show OpenLibrary icon', () => {
      component.books = [createMockBook({ description: 'OpenLibrary description', descriptionSource: 'openlibrary' })];
      fixture.detectChanges();
      const icon = fixture.nativeElement.querySelector('.leaf-icon');
      expect(icon).toBeTruthy();
    });

    it('should show retrieve summary button for books without description after index 10', () => {
      const books = Array.from({ length: 15 }, (_, i) =>
        createMockBook({ md5: `md5-${i}`, title: `Book ${i}`, description: null })
      );
      component.books = books;
      fixture.detectChanges();
      const retrieveButtons = fixture.nativeElement.querySelectorAll('.retrieve-summary-btn');
      // Books 11-15 (indices 10-14) should have the button
      expect(retrieveButtons.length).toBe(5);
    });
  });
});
