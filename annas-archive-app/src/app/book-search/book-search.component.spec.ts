import { ComponentFixture, TestBed } from '@angular/core/testing';
import { BookSearchComponent } from './book-search.component';
import { AnnaArchiveApiService } from '../services/anna-archive-api.service';
import { AuthService } from '../services/auth.service';
import { MatDialog } from '@angular/material/dialog';
import { of, throwError, NEVER, Subject } from 'rxjs';
import { BookDto } from '../models/book-dto.model';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { HttpClientTestingModule } from '@angular/common/http/testing';

describe('BookSearchComponent', () => {
  let component: BookSearchComponent;
  let fixture: ComponentFixture<BookSearchComponent>;
  let mockApiService: jasmine.SpyObj<AnnaArchiveApiService>;
  let mockAuthService: jasmine.SpyObj<AuthService>;
  let mockDialog: jasmine.SpyObj<MatDialog>;

  const createMockBook = (overrides: Partial<BookDto> = {}): BookDto => ({
    title: 'Test Book',
    md5: 'test-md5',
    authors: ['Test Author'],
    language: 'English',
    format: 'EPUB',
    source: 'annas-archive',
    fileSize: '1.5 MB',
    bookType: 'book',
    publisher: 'Test Publisher',
    year: 2024,
    isbn: null,
    coverCandidates: [],
    sendState: 'idle',
    libraryState: 'idle',
    dadsKindleState: 'idle',
    momsKindleState: 'idle',
    ...overrides
  });

  beforeEach(async () => {
    mockApiService = jasmine.createSpyObj('AnnaArchiveApiService', [
      'searchBooks',
      'suggestAuthors',
      'sendToBoox',
      'sendToLibrary',
      'sendToLibraryLibGen',
      'sendToKindle',
      'getDownloadStatus',
      'getMirrorHealth',
      'getSlumHealth',
      'getRelatedBooks'
    ]);

    mockAuthService = jasmine.createSpyObj('AuthService', ['isAuthenticated', 'isAdmin']);
    mockDialog = jasmine.createSpyObj('MatDialog', ['open']);

    // Default mock implementations
    mockAuthService.isAuthenticated.and.returnValue(true);

    // Default mock dialog ref (can be overridden in specific tests)
    // Use NEVER for afterClosed to prevent immediate emission
    const defaultDialogRef = {
      componentInstance: {
        clearStatus: jasmine.createSpy('clearStatus'),
        addStatus: jasmine.createSpy('addStatus'),
        queueCoverLookups: jasmine.createSpy('queueCoverLookups'),
        data: {}
      },
      afterClosed: () => NEVER
    };
    mockDialog.open.and.returnValue(defaultDialogRef as any);
    mockApiService.getDownloadStatus.and.returnValue(of({
      accountFastInfo: { downloadsLeft: 50, downloadsPerDay: 100 }
    }));

    mockApiService.getMirrorHealth.and.returnValue(of({
      mirrors: {
        org: { health: '95%', cert_exp: '90 days' },
        se: { health: '92%', cert_exp: '85 days' },
        li: { health: '88%', cert_exp: '80 days' },
        pm: { health: '90%', cert_exp: '75 days' },
        in: { health: '87%', cert_exp: '70 days' }
      }
    }));

    mockApiService.getSlumHealth.and.returnValue(of({
      mirrors: {
        org: { health: '95%', cert_exp: '90 days' },
        se: { health: '92%', cert_exp: '85 days' },
        li: { health: '88%', cert_exp: '80 days' },
        pm: { health: '90%', cert_exp: '75 days' },
        in: { health: '87%', cert_exp: '70 days' }
      }
    }));

    await TestBed.configureTestingModule({
      imports: [
        BookSearchComponent,
        NoopAnimationsModule,
        HttpClientTestingModule
      ],
      providers: [
        { provide: AnnaArchiveApiService, useValue: mockApiService },
        { provide: AuthService, useValue: mockAuthService },
        { provide: MatDialog, useValue: mockDialog }
      ]
    })
    .overrideComponent(BookSearchComponent, {
      set: {
        providers: [
          { provide: MatDialog, useValue: mockDialog }
        ]
      }
    })
    .compileComponents();

    fixture = TestBed.createComponent(BookSearchComponent);
    component = fixture.componentInstance;
  });

  describe('removeBook', () => {
    it('should remove a book from the books array', () => {
      // Arrange
      const book1 = createMockBook({ title: 'Book 1', md5: 'md5-1' });
      const book2 = createMockBook({ title: 'Book 2', md5: 'md5-2' });
      const book3 = createMockBook({ title: 'Book 3', md5: 'md5-3' });
      component.books = [book1, book2, book3];

      // Act
      component.removeBook(book2);

      // Assert
      expect(component.books.length).toBe(2);
      expect(component.books).toEqual([book1, book3]);
      expect(component.books).not.toContain(book2);
    });

    it('should remove the first book in the array', () => {
      // Arrange
      const book1 = createMockBook({ title: 'Book 1', md5: 'md5-1' });
      const book2 = createMockBook({ title: 'Book 2', md5: 'md5-2' });
      component.books = [book1, book2];

      // Act
      component.removeBook(book1);

      // Assert
      expect(component.books.length).toBe(1);
      expect(component.books).toEqual([book2]);
    });

    it('should remove the last book in the array', () => {
      // Arrange
      const book1 = createMockBook({ title: 'Book 1', md5: 'md5-1' });
      const book2 = createMockBook({ title: 'Book 2', md5: 'md5-2' });
      component.books = [book1, book2];

      // Act
      component.removeBook(book2);

      // Assert
      expect(component.books.length).toBe(1);
      expect(component.books).toEqual([book1]);
    });

    it('should handle removing a book that does not exist', () => {
      // Arrange
      const book1 = createMockBook({ title: 'Book 1', md5: 'md5-1' });
      const book2 = createMockBook({ title: 'Book 2', md5: 'md5-2' });
      const bookNotInArray = createMockBook({ title: 'Not in Array', md5: 'md5-99' });
      component.books = [book1, book2];

      // Act
      component.removeBook(bookNotInArray);

      // Assert - array should remain unchanged
      expect(component.books.length).toBe(2);
      expect(component.books).toEqual([book1, book2]);
    });

    it('should handle removing from an empty array', () => {
      // Arrange
      component.books = [];
      const book = createMockBook();

      // Act
      component.removeBook(book);

      // Assert
      expect(component.books.length).toBe(0);
    });

    it('should remove only one instance when duplicate books exist', () => {
      // Arrange
      const book1 = createMockBook({ title: 'Book 1', md5: 'md5-1' });
      const book2 = createMockBook({ title: 'Book 2', md5: 'md5-2' });
      component.books = [book1, book2, book1]; // book1 appears twice

      // Act
      component.removeBook(book1);

      // Assert - only first instance should be removed
      expect(component.books.length).toBe(2);
      expect(component.books).toEqual([book2, book1]);
    });

    it('should work correctly after removing all books one by one', () => {
      // Arrange
      const book1 = createMockBook({ title: 'Book 1', md5: 'md5-1' });
      const book2 = createMockBook({ title: 'Book 2', md5: 'md5-2' });
      const book3 = createMockBook({ title: 'Book 3', md5: 'md5-3' });
      component.books = [book1, book2, book3];

      // Act
      component.removeBook(book1);
      expect(component.books.length).toBe(2);

      component.removeBook(book2);
      expect(component.books.length).toBe(1);

      component.removeBook(book3);

      // Assert
      expect(component.books.length).toBe(0);
      expect(component.books).toEqual([]);
    });
  });

  describe('filteredBooks with removed books', () => {
    it('should not include removed books in filtered results', () => {
      // Arrange
      const book1 = createMockBook({ title: 'Book 1', format: 'EPUB' });
      const book2 = createMockBook({ title: 'Book 2', format: 'PDF' });
      const book3 = createMockBook({ title: 'Book 3', format: 'EPUB' });
      component.books = [book1, book2, book3];
      component.selectedFormat = 'EPUB';

      // Act - remove book3
      component.removeBook(book3);
      const filtered = component.filteredBooks;

      // Assert
      expect(filtered.length).toBe(1);
      expect(filtered).toEqual([book1]);
      expect(filtered).not.toContain(book3);
    });

    it('should update filteredBooks reactively when a book is removed', () => {
      // Arrange
      const book1 = createMockBook({ title: 'Book 1', authors: ['Author A'] });
      const book2 = createMockBook({ title: 'Book 2', authors: ['Author B'] });
      component.books = [book1, book2];
      component.selectedAuthor = '';

      // Initial filter
      let filtered = component.filteredBooks;
      expect(filtered.length).toBe(2);

      // Act - remove one book
      component.removeBook(book1);
      filtered = component.filteredBooks;

      // Assert
      expect(filtered.length).toBe(1);
      expect(filtered).toEqual([book2]);
    });
  });

  describe('Component initialization', () => {
    it('should initialize with empty books array', () => {
      expect(component.books).toEqual([]);
    });

    it('should initialize search term as empty string', () => {
      expect(component.searchTerm).toBe('');
    });

    it('should initialize with no selected format', () => {
      expect(component.selectedFormat).toBe('');
    });

    it('should initialize with no selected author', () => {
      expect(component.selectedAuthor).toBe('');
    });

    it('should fetch download status on init', () => {
      // Act
      fixture.detectChanges(); // triggers ngOnInit

      // Assert
      expect(mockApiService.getDownloadStatus).toHaveBeenCalled();
    });

    it('should fetch mirror health on init', () => {
      // Act
      fixture.detectChanges(); // triggers ngOnInit

      // Assert
      expect(mockApiService.getSlumHealth).toHaveBeenCalled();
    });
  });

  describe('Download counter', () => {
    it('should update download counter from server response', () => {
      // Arrange - Create fresh component with new mock values
      mockApiService.getDownloadStatus.and.returnValue(of({
        accountFastInfo: { downloadsLeft: 25, downloadsPerDay: 100 }
      }));

      // Create new component instance
      const newFixture = TestBed.createComponent(BookSearchComponent);
      const newComponent = newFixture.componentInstance;

      // Act
      newFixture.detectChanges();

      // Assert
      expect(newComponent.downloadsLeft).toBe(25);
      expect(newComponent.downloadsPerDay).toBe(100);
    });

    it('should calculate warning level as red when downloads left <= 10', () => {
      // Arrange
      component.downloadsLeft = 8;

      // Act
      const warningLevel = component.downloadWarningLevel;

      // Assert
      expect(warningLevel).toBe('red');
    });

    it('should calculate warning level as orange when downloads left <= 20', () => {
      // Arrange
      component.downloadsLeft = 15;

      // Act
      const warningLevel = component.downloadWarningLevel;

      // Assert
      expect(warningLevel).toBe('orange');
    });

    it('should calculate warning level as yellow when downloads left <= 30', () => {
      // Arrange
      component.downloadsLeft = 25;

      // Act
      const warningLevel = component.downloadWarningLevel;

      // Assert
      expect(warningLevel).toBe('yellow');
    });

    it('should calculate warning level as none when downloads left > 30', () => {
      // Arrange
      component.downloadsLeft = 50;

      // Act
      const warningLevel = component.downloadWarningLevel;

      // Assert
      expect(warningLevel).toBe('none');
    });

    it('should calculate warning level as none when downloadsLeft is null', () => {
      // Arrange
      component.downloadsLeft = null;

      // Act
      const warningLevel = component.downloadWarningLevel;

      // Assert
      expect(warningLevel).toBe('none');
    });
  });

  describe('Format filtering', () => {
    it('should filter books by selected format', () => {
      // Arrange
      const epubBook = createMockBook({ format: 'EPUB' });
      const pdfBook = createMockBook({ format: 'PDF' });
      const mobiBook = createMockBook({ format: 'MOBI' });
      component.books = [epubBook, pdfBook, mobiBook];
      component.selectedFormat = 'EPUB';

      // Act
      const filtered = component.filteredBooks;

      // Assert
      expect(filtered.length).toBe(1);
      expect(filtered[0]).toEqual(epubBook);
    });

    it('should return all books when no format is selected', () => {
      // Arrange
      const book1 = createMockBook({ format: 'EPUB' });
      const book2 = createMockBook({ format: 'PDF' });
      component.books = [book1, book2];
      component.selectedFormat = '';

      // Act
      const filtered = component.filteredBooks;

      // Assert
      expect(filtered.length).toBe(2);
      expect(filtered).toEqual([book1, book2]);
    });
  });

  describe('Author filtering', () => {
    it('should filter books by selected author', () => {
      // Arrange
      const book1 = createMockBook({ authors: ['Stephen King'] });
      const book2 = createMockBook({ authors: ['J.K. Rowling'] });
      const book3 = createMockBook({ authors: ['Stephen King', 'Peter Straub'] });
      component.books = [book1, book2, book3];
      component.selectedAuthor = 'Stephen King';

      // Act
      const filtered = component.filteredBooks;

      // Assert
      expect(filtered.length).toBe(2);
      expect(filtered).toContain(book1);
      expect(filtered).toContain(book3);
      expect(filtered).not.toContain(book2);
    });

    it('should return all books when no author is selected', () => {
      // Arrange
      const book1 = createMockBook({ authors: ['Author A'] });
      const book2 = createMockBook({ authors: ['Author B'] });
      component.books = [book1, book2];
      component.selectedAuthor = '';

      // Act
      const filtered = component.filteredBooks;

      // Assert
      expect(filtered.length).toBe(2);
    });
  });

  describe('Combined filtering', () => {
    it('should filter by both format and author', () => {
      // Arrange
      const book1 = createMockBook({ format: 'EPUB', authors: ['Author A'] });
      const book2 = createMockBook({ format: 'PDF', authors: ['Author A'] });
      const book3 = createMockBook({ format: 'EPUB', authors: ['Author B'] });
      component.books = [book1, book2, book3];
      component.selectedFormat = 'EPUB';
      component.selectedAuthor = 'Author A';

      // Act
      const filtered = component.filteredBooks;

      // Assert
      expect(filtered.length).toBe(1);
      expect(filtered[0]).toEqual(book1);
    });
  });

  describe('Available formats', () => {
    it('should return static list of available formats', () => {
      // Act
      const formats = component.availableFormats;

      // Assert
      expect(formats).toContain('EPUB');
      expect(formats).toContain('MOBI');
      expect(formats).toContain('PDF');
      expect(formats).toContain('AZW3');
      expect(formats).toContain('FB2');
      expect(formats).toContain('TXT');
    });
  });

  describe('Health status', () => {
    it('should return health-green for health >= 90', () => {
      expect(component.getHealthColorClass(95)).toBe('health-green');
      expect(component.getHealthColorClass(90)).toBe('health-green');
    });

    it('should return health-yellow for health >= 70 and < 90', () => {
      expect(component.getHealthColorClass(85)).toBe('health-yellow');
      expect(component.getHealthColorClass(70)).toBe('health-yellow');
    });

    it('should return health-orange for health >= 50 and < 70', () => {
      expect(component.getHealthColorClass(65)).toBe('health-orange');
      expect(component.getHealthColorClass(50)).toBe('health-orange');
    });

    it('should return health-red for health < 50', () => {
      expect(component.getHealthColorClass(45)).toBe('health-red');
      expect(component.getHealthColorClass(0)).toBe('health-red');
    });

    it('should return health-unknown for null health', () => {
      expect(component.getHealthColorClass(null)).toBe('health-unknown');
    });
  });

  describe('Format dropdown locking during matching', () => {
    it('should have format dropdown enabled by default', () => {
      expect(component.relatedBooksModalOpen).toBe(false);
    });

    it('should allow setting relatedBooksModalOpen to true', () => {
      component.relatedBooksModalOpen = true;
      expect(component.relatedBooksModalOpen).toBe(true);
    });

    it('should disable format dropdown when related books modal opens', () => {
      // Verify initial state
      expect(component.relatedBooksModalOpen).toBe(false);

      component.searchTerm = 'Test Book';
      component.selectedAuthor = 'Test Author';

      // Setup required API mock
      mockApiService.getRelatedBooks.and.returnValue(of({ sameSeries: [], otherSeries: [], seriesSummary: null }));

      component.openRelatedBooksModal();

      expect(mockDialog.open).toHaveBeenCalled();
      expect(component.relatedBooksModalOpen).toBe(true);
    });

    it('should re-enable format dropdown when related books modal closes', (done) => {
      component.searchTerm = 'Test Book';
      component.selectedAuthor = 'Test Author';

      // Create a subject to control when the dialog closes
      const dialogCloseSubject = new Subject();

      // Mock dialog that we can close manually
      const mockDialogRef = {
        componentInstance: {
          clearStatus: jasmine.createSpy('clearStatus'),
          addStatus: jasmine.createSpy('addStatus'),
          queueCoverLookups: jasmine.createSpy('queueCoverLookups'),
          data: {}
        },
        afterClosed: () => dialogCloseSubject.asObservable()
      };
      mockDialog.open.and.returnValue(mockDialogRef as any);
      mockApiService.getRelatedBooks.and.returnValue(of({ sameSeries: [], otherSeries: [], seriesSummary: null }));

      component.openRelatedBooksModal();
      expect(component.relatedBooksModalOpen).toBe(true);

      // Close the dialog
      dialogCloseSubject.next({});
      dialogCloseSubject.complete();

      // Wait for the subscription to process
      setTimeout(() => {
        expect(component.relatedBooksModalOpen).toBe(false);
        done();
      }, 10);
    });

    it('should not open modal if searchTerm is empty', () => {
      component.searchTerm = '';
      component.selectedAuthor = 'Test Author';

      component.openRelatedBooksModal();

      expect(component.relatedBooksModalOpen).toBe(false);
      expect(mockDialog.open).not.toHaveBeenCalled();
    });

    it('should not open modal if selectedAuthor is empty', () => {
      component.searchTerm = 'Test Book';
      component.selectedAuthor = '';

      component.openRelatedBooksModal();

      expect(component.relatedBooksModalOpen).toBe(false);
      expect(mockDialog.open).not.toHaveBeenCalled();
    });
  });
});
