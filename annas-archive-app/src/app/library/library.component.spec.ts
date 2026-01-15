import { ComponentFixture, TestBed } from '@angular/core/testing';
import { LibraryComponent } from './library.component';
import { LibraryApiService } from '../services/library-api.service';
import { MatDialog } from '@angular/material/dialog';
import { AuthService } from '../services/auth.service';
import { of, throwError } from 'rxjs';
import { BookEditDialogResult } from '../components/book-edit-dialog/book-edit-dialog.component';

describe('LibraryComponent', () => {
  let component: LibraryComponent;
  let fixture: ComponentFixture<LibraryComponent>;
  let mockLibraryApiService: jasmine.SpyObj<LibraryApiService>;
  let mockDialog: jasmine.SpyObj<MatDialog>;
  let mockAuthService: jasmine.SpyObj<AuthService>;

  beforeEach(async () => {
    mockLibraryApiService = jasmine.createSpyObj('LibraryApiService', [
      'getLibraryBooks',
      'updateLibraryBookMetadata',
      'updateLibraryBookCover',
      'uploadLibraryBookCoverBytes',
      'updateLibraryBookRatings',
      'deleteLibraryBook',
      'sendLibraryToKindle',
      'wipeLibraryGenres',
      'updateLibraryBookReaderEnabled'
    ]);

    mockDialog = jasmine.createSpyObj('MatDialog', ['open']);
    mockAuthService = jasmine.createSpyObj('AuthService', ['isAdmin']);

    // Default mock returns
    mockLibraryApiService.getLibraryBooks.and.returnValue(of([]));
    mockAuthService.isAdmin.and.returnValue(true);

    await TestBed.configureTestingModule({
      imports: [LibraryComponent],
      providers: [
        { provide: LibraryApiService, useValue: mockLibraryApiService },
        { provide: MatDialog, useValue: mockDialog },
        { provide: AuthService, useValue: mockAuthService }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(LibraryComponent);
    component = fixture.componentInstance;
  });

  describe('Book Cover Updates', () => {
    it('should update book cover URL when dialog returns coverUrl', (done) => {
      // Arrange
      const testBook = {
        title: 'Test Book',
        authors: ['Test Author'],
        format: 'EPUB',
        fileSize: '1.2 MB',
        fileName: 'test-book.epub',
        coverUrl: 'http://example.com/old-cover.jpg',
        primaryGenre: 'Fiction',
        tags: ['test'],
        series: null,
        source: null,
        md5: null,
        savedAt: null,
        genres: ['Fiction'],
        publishedDate: null,
        pages: null,
        goodreadsRating: null,
        personalRating: null,
        dadsKindleState: undefined,
        momsKindleState: undefined,
        readerEnabled: false
      };

      component.books = [testBook];

      const newCoverUrl = 'http://example.com/new-cover.jpg';
      const dialogResult: BookEditDialogResult = {
        coverUrl: newCoverUrl,
        primaryGenre: 'Fiction',
        tags: ['test'],
        series: null,
        title: 'Test Book',
        authors: ['Test Author']
      };

      const mockDialogRef = {
        afterClosed: () => of(dialogResult)
      };

      mockDialog.open.and.returnValue(mockDialogRef as any);
      mockLibraryApiService.updateLibraryBookMetadata.and.returnValue(of({
        fileName: 'test-book.epub',
        title: 'Test Book',
        authors: ['Test Author'],
        format: 'EPUB',
        fileSize: '1.2 MB',
        primaryGenre: 'Fiction',
        tags: ['test']
      }));
      mockLibraryApiService.updateLibraryBookCover.and.returnValue(
        of({ coverUrl: newCoverUrl })
      );

      // Mock fetch to fail immediately, triggering fallback to URL method
      spyOn(window, 'fetch').and.returnValue(Promise.reject(new Error('CORS error')));

      // Act
      component.onCoverClick(testBook);

      // Assert - Cover update should be called (after blob fetch fails and falls back to URL method)
      setTimeout(() => {
        expect(mockLibraryApiService.updateLibraryBookCover).toHaveBeenCalledWith(
          'test-book.epub',
          newCoverUrl
        );

        // Book's coverUrl should be updated with cache-busting timestamp
        expect(testBook.coverUrl).toContain(newCoverUrl);
        expect(testBook.coverUrl).toContain('?t=');
        done();
      }, 150);
    });

    it('should not call updateLibraryBookCover when dialog returns no coverUrl', (done) => {
      // Arrange
      const testBook = {
        title: 'Test Book',
        authors: ['Test Author'],
        format: 'EPUB',
        fileSize: '1.2 MB',
        fileName: 'test-book.epub',
        coverUrl: 'http://example.com/old-cover.jpg',
        primaryGenre: 'Fiction',
        tags: ['test'],
        series: null,
        source: null,
        md5: null,
        savedAt: null,
        genres: ['Fiction'],
        publishedDate: null,
        pages: null,
        goodreadsRating: null,
        personalRating: null,
        dadsKindleState: undefined,
        momsKindleState: undefined,
        readerEnabled: false
      };

      component.books = [testBook];

      const dialogResult: BookEditDialogResult = {
        primaryGenre: 'Fiction',
        tags: ['test'],
        series: null,
        title: 'Test Book',
        authors: ['Test Author']
        // No coverUrl
      };

      const mockDialogRef = {
        afterClosed: () => of(dialogResult)
      };

      mockDialog.open.and.returnValue(mockDialogRef as any);
      mockLibraryApiService.updateLibraryBookMetadata.and.returnValue(of({
        fileName: 'test-book.epub',
        title: 'Test Book',
        authors: ['Test Author'],
        format: 'EPUB',
        fileSize: '1.2 MB',
        primaryGenre: 'Fiction',
        tags: ['test']
      }));

      // Act
      component.onCoverClick(testBook);

      // Assert - Cover update should NOT be called
      setTimeout(() => {
        expect(mockLibraryApiService.updateLibraryBookCover).not.toHaveBeenCalled();
        done();
      }, 150);
    });

    it('should handle cover update API error gracefully', (done) => {
      // Arrange
      const testBook = {
        title: 'Test Book',
        authors: ['Test Author'],
        format: 'EPUB',
        fileSize: '1.2 MB',
        fileName: 'test-book.epub',
        coverUrl: 'http://example.com/old-cover.jpg',
        primaryGenre: 'Fiction',
        tags: ['test'],
        series: null,
        source: null,
        md5: null,
        savedAt: null,
        genres: ['Fiction'],
        publishedDate: null,
        pages: null,
        goodreadsRating: null,
        personalRating: null,
        dadsKindleState: undefined,
        momsKindleState: undefined,
        readerEnabled: false
      };

      component.books = [testBook];

      const newCoverUrl = 'http://example.com/new-cover.jpg';
      const dialogResult: BookEditDialogResult = {
        coverUrl: newCoverUrl,
        primaryGenre: 'Fiction',
        tags: ['test'],
        series: null,
        title: 'Test Book',
        authors: ['Test Author']
      };

      const mockDialogRef = {
        afterClosed: () => of(dialogResult)
      };

      mockDialog.open.and.returnValue(mockDialogRef as any);
      mockLibraryApiService.updateLibraryBookMetadata.and.returnValue(of({
        fileName: 'test-book.epub',
        title: 'Test Book',
        authors: ['Test Author'],
        format: 'EPUB',
        fileSize: '1.2 MB',
        primaryGenre: 'Fiction',
        tags: ['test']
      }));
      mockLibraryApiService.updateLibraryBookCover.and.returnValue(
        throwError(() => new Error('API Error'))
      );

      // Mock fetch to fail immediately, triggering fallback to URL method
      spyOn(window, 'fetch').and.returnValue(Promise.reject(new Error('CORS error')));
      spyOn(console, 'error');

      // Act
      component.onCoverClick(testBook);

      // Assert - Error should be logged, but component should not crash
      setTimeout(() => {
        expect(console.error).toHaveBeenCalled();
        expect(testBook.coverUrl).toBe('http://example.com/old-cover.jpg'); // Unchanged
        done();
      }, 150);
    });

    it('should add cache-busting timestamp to cover URL with existing query params', (done) => {
      // Arrange
      const testBook = {
        title: 'Test Book',
        authors: ['Test Author'],
        format: 'EPUB',
        fileSize: '1.2 MB',
        fileName: 'test-book.epub',
        coverUrl: 'http://example.com/old-cover.jpg',
        primaryGenre: 'Fiction',
        tags: ['test'],
        series: null,
        source: null,
        md5: null,
        savedAt: null,
        genres: ['Fiction'],
        publishedDate: null,
        pages: null,
        goodreadsRating: null,
        personalRating: null,
        dadsKindleState: undefined,
        momsKindleState: undefined,
        readerEnabled: false
      };

      component.books = [testBook];

      const newCoverUrl = 'http://example.com/new-cover.jpg?size=large';
      const dialogResult: BookEditDialogResult = {
        coverUrl: newCoverUrl,
        primaryGenre: 'Fiction',
        tags: ['test'],
        series: null,
        title: 'Test Book',
        authors: ['Test Author']
      };

      const mockDialogRef = {
        afterClosed: () => of(dialogResult)
      };

      mockDialog.open.and.returnValue(mockDialogRef as any);
      mockLibraryApiService.updateLibraryBookMetadata.and.returnValue(of({
        fileName: 'test-book.epub',
        title: 'Test Book',
        authors: ['Test Author'],
        format: 'EPUB',
        fileSize: '1.2 MB',
        primaryGenre: 'Fiction',
        tags: ['test']
      }));
      mockLibraryApiService.updateLibraryBookCover.and.returnValue(
        of({ coverUrl: newCoverUrl })
      );

      // Mock fetch to fail immediately, triggering fallback to URL method
      spyOn(window, 'fetch').and.returnValue(Promise.reject(new Error('CORS error')));

      // Act
      component.onCoverClick(testBook);

      // Assert - Should use & instead of ? for timestamp (after blob fetch fails and falls back to URL method)
      setTimeout(() => {
        expect(testBook.coverUrl).toContain(newCoverUrl);
        expect(testBook.coverUrl).toContain('&t=');
        expect(testBook.coverUrl).not.toContain('?t=');
        done();
      }, 150);
    });
  });

  describe('Bulk Edit Mode', () => {
    it('should include owner tags in bulk edit dialog genre list', () => {
      // Arrange
      const testBooks = [
        {
          title: 'Book 1',
          authors: ['Author 1'],
          format: 'EPUB',
          fileSize: '1.2 MB',
          fileName: 'book1.epub',
          coverUrl: null,
          primaryGenre: 'Science Fiction',
          tags: [],
          series: null,
          source: null,
          md5: null,
          savedAt: null,
          genres: [],
          publishedDate: null,
          pages: null,
          goodreadsRating: null,
          personalRating: null,
          dadsKindleState: undefined,
          momsKindleState: undefined,
          readerEnabled: false
        },
        {
          title: 'Book 2',
          authors: ['Author 2'],
          format: 'EPUB',
          fileSize: '1.5 MB',
          fileName: 'book2.epub',
          coverUrl: null,
          primaryGenre: "Dad's Books",
          tags: ["Mom's Books"],
          series: null,
          source: null,
          md5: null,
          savedAt: null,
          genres: [],
          publishedDate: null,
          pages: null,
          goodreadsRating: null,
          personalRating: null,
          dadsKindleState: undefined,
          momsKindleState: undefined,
          readerEnabled: false
        }
      ];

      component.books = testBooks;
      component.bulkEditMode = true;
      component.selectedBooksForBulk.add('book1.epub');
      component.selectedBooksForBulk.add('book2.epub');

      mockDialog.open.and.returnValue({
        afterClosed: () => of(undefined)
      } as any);

      // Act
      component.openBulkEditDialog();

      // Assert
      expect(mockDialog.open).toHaveBeenCalled();
      const callArgs = mockDialog.open.calls.mostRecent().args;
      const dialogConfig = callArgs[1];
      expect(dialogConfig).toBeDefined();
      const dialogData = dialogConfig!.data as any;
      expect(dialogData.availableGenres).toContain("Dad's Books");
      expect(dialogData.availableGenres).toContain("Mom's Books");
      expect(dialogData.availableGenres).toContain("Paul's Books");
      expect(dialogData.availableGenres).toContain('Science Fiction');
    });

    it('should not include owner tags in regular genre filter list', () => {
      // Arrange
      const testBooks = [
        {
          title: 'Book 1',
          authors: ['Author 1'],
          format: 'EPUB',
          fileSize: '1.2 MB',
          fileName: 'book1.epub',
          coverUrl: null,
          primaryGenre: "Dad's Books",
          tags: ["Mom's Books", 'Fantasy'],
          series: null,
          source: null,
          md5: null,
          savedAt: null,
          genres: [],
          publishedDate: null,
          pages: null,
          goodreadsRating: null,
          personalRating: null,
          dadsKindleState: 'idle' as const,
          momsKindleState: 'idle' as const,
          readerEnabled: false
        }
      ];

      mockLibraryApiService.getLibraryBooks.and.returnValue(of(testBooks));

      // Act - trigger ngOnInit which loads books and builds genre list
      component.ngOnInit();

      // Assert - owner tags should NOT be in the regular genre list
      expect(component.genres).not.toContain("Dad's Books");
      expect(component.genres).not.toContain("Mom's Books");
      expect(component.genres).not.toContain("Paul's Books");
      expect(component.genres).toContain('Fantasy');
    });
  });

  describe('Tile Size Controls', () => {
    it('should default to medium tile size', () => {
      expect(component.tileSize).toBe('medium');
    });

    it('should change tile size to small when setTileSize is called with small', () => {
      component.setTileSize('small');
      expect(component.tileSize).toBe('small');
    });

    it('should change tile size to large when setTileSize is called with large', () => {
      component.setTileSize('large');
      expect(component.tileSize).toBe('large');
    });

    it('should change tile size back to medium', () => {
      component.setTileSize('large');
      expect(component.tileSize).toBe('large');

      component.setTileSize('medium');
      expect(component.tileSize).toBe('medium');
    });
  });

  describe('Virtual Scrolling', () => {
    const createTestBooks = (count: number) => {
      return Array.from({ length: count }, (_, i) => ({
        title: `Book ${i + 1}`,
        authors: [`Author ${i + 1}`],
        format: 'EPUB',
        fileSize: '1.2 MB',
        fileName: `book-${i + 1}.epub`,
        coverUrl: null,
        primaryGenre: 'Fiction',
        tags: [],
        series: null,
        source: null,
        md5: null,
        savedAt: new Date(2024, 0, i + 1).toISOString(),
        publishedDate: null,
        pages: null,
        goodreadsRating: null,
        personalRating: null,
        dadsKindleState: 'idle' as const,
        momsKindleState: 'idle' as const,
        readerEnabled: false
      }));
    };

    describe('rowHeight getter', () => {
      it('should return 400 for medium tile size', () => {
        component.tileSize = 'medium';
        expect(component.rowHeight).toBe(400);
      });

      it('should return 320 for small tile size', () => {
        component.tileSize = 'small';
        expect(component.rowHeight).toBe(320);
      });

      it('should return 480 for large tile size', () => {
        component.tileSize = 'large';
        expect(component.rowHeight).toBe(480);
      });
    });

    describe('bookRows getter', () => {
      it('should group books into rows based on cachedItemsPerRow', () => {
        component.books = createTestBooks(15);
        // Access private property for testing
        (component as any).cachedItemsPerRow = 5;

        const rows = component.bookRows;

        expect(rows.length).toBe(3);
        expect(rows[0].length).toBe(5);
        expect(rows[1].length).toBe(5);
        expect(rows[2].length).toBe(5);
      });

      it('should handle partial last row correctly', () => {
        component.books = createTestBooks(13);
        (component as any).cachedItemsPerRow = 5;

        const rows = component.bookRows;

        expect(rows.length).toBe(3);
        expect(rows[0].length).toBe(5);
        expect(rows[1].length).toBe(5);
        expect(rows[2].length).toBe(3); // Partial row
      });

      it('should return empty array for empty books', () => {
        component.books = [];
        (component as any).cachedItemsPerRow = 5;

        const rows = component.bookRows;

        expect(rows.length).toBe(0);
      });

      it('should handle single book', () => {
        component.books = createTestBooks(1);
        (component as any).cachedItemsPerRow = 5;

        const rows = component.bookRows;

        expect(rows.length).toBe(1);
        expect(rows[0].length).toBe(1);
      });
    });

    describe('trackByRow', () => {
      it('should return first book filename as tracking key', () => {
        const row = createTestBooks(3);
        const key = component.trackByRow(0, row);
        expect(key).toBe('book-1.epub');
      });

      it('should return fallback key for empty row', () => {
        const key = component.trackByRow(5, []);
        expect(key).toBe('row-5');
      });

      it('should handle row with undefined filename', () => {
        const row = [{ ...createTestBooks(1)[0], fileName: '' }];
        const key = component.trackByRow(3, row);
        expect(key).toBe('row-3');
      });
    });

    describe('setTileSize with recalculateLayout', () => {
      it('should trigger recalculateLayout after tile size change', (done) => {
        spyOn(component, 'recalculateLayout');

        component.setTileSize('small');

        setTimeout(() => {
          expect(component.recalculateLayout).toHaveBeenCalled();
          done();
        }, 10);
      });
    });

    describe('resetView with virtual scroll', () => {
      it('should reset tile size to medium and trigger recalculateLayout', (done) => {
        component.tileSize = 'large';
        spyOn(component, 'recalculateLayout');

        component.resetView();

        expect(component.tileSize).toBe('medium');
        setTimeout(() => {
          expect(component.recalculateLayout).toHaveBeenCalled();
          done();
        }, 10);
      });

      it('should reset activeLetter to #', () => {
        component.activeLetter = 'M';

        component.resetView();

        expect(component.activeLetter).toBe('#');
      });
    });

    describe('scrollToLetter', () => {
      beforeEach(() => {
        // Create books with different starting letters
        const books = [
          { ...createTestBooks(1)[0], title: 'Alpha Book', fileName: 'alpha.epub' },
          { ...createTestBooks(1)[0], title: 'Beta Book', fileName: 'beta.epub' },
          { ...createTestBooks(1)[0], title: 'Charlie Book', fileName: 'charlie.epub' },
          { ...createTestBooks(1)[0], title: 'Delta Book', fileName: 'delta.epub' },
          { ...createTestBooks(1)[0], title: 'Echo Book', fileName: 'echo.epub' },
          { ...createTestBooks(1)[0], title: 'Foxtrot Book', fileName: 'foxtrot.epub' }
        ];
        component.books = books;
        component.sortOrder = 'title';
        (component as any).cachedItemsPerRow = 2;
      });

      it('should not scroll to unavailable letter', () => {
        // Z is not available since no books start with Z
        component.scrollToLetter('Z');
        // activeLetter should remain unchanged
        expect(component.activeLetter).toBe('#');
      });

      it('should update activeLetter when scrolling to valid letter', () => {
        // Mock the virtualScroll
        (component as any).virtualScroll = {
          scrollToIndex: jasmine.createSpy('scrollToIndex')
        };

        component.scrollToLetter('C');

        expect(component.activeLetter).toBe('C');
      });

      it('should calculate correct row index for letter', () => {
        const mockScrollToIndex = jasmine.createSpy('scrollToIndex');
        (component as any).virtualScroll = {
          scrollToIndex: mockScrollToIndex
        };
        (component as any).cachedItemsPerRow = 2;

        // C is the 3rd book (index 2), with 2 items per row, that's row 1
        component.scrollToLetter('C');

        expect(mockScrollToIndex).toHaveBeenCalledWith(1, 'smooth');
      });
    });

    describe('onSortChange with virtual scroll', () => {
      it('should scroll to top when sort changes', () => {
        const mockScrollToIndex = jasmine.createSpy('scrollToIndex');
        (component as any).virtualScroll = {
          scrollToIndex: mockScrollToIndex
        };

        component.onSortChange();

        expect(mockScrollToIndex).toHaveBeenCalledWith(0);
      });

      it('should update activeLetter based on first book after sort', () => {
        component.books = [
          { ...createTestBooks(1)[0], title: 'Zebra Book' }
        ];
        component.sortOrder = 'title';
        (component as any).virtualScroll = {
          scrollToIndex: jasmine.createSpy('scrollToIndex')
        };

        component.onSortChange();

        expect(component.activeLetter).toBe('Z');
      });

      it('should set activeLetter to # for non-alpha sorts', () => {
        component.books = createTestBooks(5);
        component.sortOrder = 'recent';
        (component as any).virtualScroll = {
          scrollToIndex: jasmine.createSpy('scrollToIndex')
        };

        component.onSortChange();

        expect(component.activeLetter).toBe('#');
      });
    });
  });
});
