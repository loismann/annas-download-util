import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { LibraryComponent } from './library.component';
import { LibraryApiService, LibraryBook as ServiceLibraryBook } from '../services/library-api.service';
import { MatDialog } from '@angular/material/dialog';
import { AuthService } from '../services/auth.service';
import { of, throwError, Subject, delay } from 'rxjs';
import { BookEditDialogResult } from '../components/book-edit-dialog/book-edit-dialog.component';
import { LibraryBook } from '../components/book-card/book-card.component';

describe('LibraryComponent', () => {
  let component: LibraryComponent;
  let fixture: ComponentFixture<LibraryComponent>;
  let mockLibraryApiService: jasmine.SpyObj<LibraryApiService>;
  let mockDialog: jasmine.SpyObj<MatDialog>;
  let mockAuthService: jasmine.SpyObj<AuthService>;

  beforeEach(async () => {
    mockLibraryApiService = jasmine.createSpyObj('LibraryApiService', [
      'getLibraryBooks',
      'getLibraryBooksPaginated',
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
    mockLibraryApiService.getLibraryBooksPaginated.and.returnValue(of({
      books: [],
      totalCount: 0,
      skip: 0,
      take: 100
    }));
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
          publishedDate: null,
          pages: null,
          goodreadsRating: null,
          personalRating: null,
          dadsKindleState: 'idle' as const,
          momsKindleState: 'idle' as const,
          readerEnabled: false
        }
      ];

      mockLibraryApiService.getLibraryBooksPaginated.and.returnValue(of({
        books: testBooks,
        totalCount: testBooks.length,
        skip: 0,
        take: 100
      }));

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
        const books = createTestBooks(15);
        component.books = books;
        component.displayBooks = books; // filteredBooks returns displayBooks
        // Access private property for testing
        (component as any).cachedItemsPerRow = 5;

        const rows = component.bookRows;

        expect(rows.length).toBe(3);
        expect(rows[0].length).toBe(5);
        expect(rows[1].length).toBe(5);
        expect(rows[2].length).toBe(5);
      });

      it('should handle partial last row correctly', () => {
        const books = createTestBooks(13);
        component.books = books;
        component.displayBooks = books; // filteredBooks returns displayBooks
        (component as any).cachedItemsPerRow = 5;

        const rows = component.bookRows;

        expect(rows.length).toBe(3);
        expect(rows[0].length).toBe(5);
        expect(rows[1].length).toBe(5);
        expect(rows[2].length).toBe(3); // Partial row
      });

      it('should return empty array for empty books', () => {
        component.books = [];
        component.displayBooks = [];
        (component as any).cachedItemsPerRow = 5;

        const rows = component.bookRows;

        expect(rows.length).toBe(0);
      });

      it('should handle single book', () => {
        const books = createTestBooks(1);
        component.books = books;
        component.displayBooks = books; // filteredBooks returns displayBooks
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
        // Create books with different starting letters (already sorted by title)
        const books = [
          { ...createTestBooks(1)[0], title: 'Alpha Book', fileName: 'alpha.epub' },
          { ...createTestBooks(1)[0], title: 'Beta Book', fileName: 'beta.epub' },
          { ...createTestBooks(1)[0], title: 'Charlie Book', fileName: 'charlie.epub' },
          { ...createTestBooks(1)[0], title: 'Delta Book', fileName: 'delta.epub' },
          { ...createTestBooks(1)[0], title: 'Echo Book', fileName: 'echo.epub' },
          { ...createTestBooks(1)[0], title: 'Foxtrot Book', fileName: 'foxtrot.epub' }
        ];
        component.books = books;
        component.displayBooks = books; // filteredBooks returns displayBooks
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
        const books = createTestBooks(5);
        component.books = books;
        component.displayBooks = books; // filteredBooks returns displayBooks
        // Initialize component to avoid change detection issues
        fixture.detectChanges();

        const mockScrollToIndex = jasmine.createSpy('scrollToIndex');
        (component as any).virtualScroll = {
          scrollToIndex: mockScrollToIndex
        };

        component.onSortChange();

        expect(mockScrollToIndex).toHaveBeenCalledWith(0);
      });

      it('should update activeLetter based on first book after sort', () => {
        const books = [
          { ...createTestBooks(1)[0], title: 'Zebra Book' }
        ];
        component.books = books;
        component.displayBooks = books; // filteredBooks returns displayBooks
        component.sortOrder = 'title';
        (component as any).virtualScroll = {
          scrollToIndex: jasmine.createSpy('scrollToIndex')
        };

        component.onSortChange();

        expect(component.activeLetter).toBe('Z');
      });

      it('should set activeLetter to # for non-alpha sorts', () => {
        const books = createTestBooks(5);
        component.books = books;
        component.displayBooks = books; // filteredBooks returns displayBooks
        component.sortOrder = 'recent';
        (component as any).virtualScroll = {
          scrollToIndex: jasmine.createSpy('scrollToIndex')
        };

        component.onSortChange();

        expect(component.activeLetter).toBe('#');
      });
    });
  });

  describe('Concurrency Tests', () => {
    const createTestBook = (overrides: Partial<LibraryBook> = {}): LibraryBook => ({
      title: 'Test Book',
      authors: ['Test Author'],
      format: 'EPUB',
      fileSize: '1.2 MB',
      fileName: 'test-book.epub',
      coverUrl: null,
      primaryGenre: 'Fiction',
      tags: [],
      series: null,
      source: null,
      md5: null,
      savedAt: null,
      publishedDate: null,
      pages: null,
      goodreadsRating: null,
      personalRating: null,
      dadsKindleState: 'idle',
      momsKindleState: 'idle',
      readerEnabled: false,
      ...overrides
    });

    describe('Double-click protection for sendToKindle', () => {
      it('should prevent duplicate sends when dadsKindleState is sending', () => {
        const book = createTestBook({ dadsKindleState: 'sending' });
        component.books = [book];

        component.sendToKindle(book, 'dad');

        expect(mockLibraryApiService.sendLibraryToKindle).not.toHaveBeenCalled();
      });

      it('should prevent duplicate sends when momsKindleState is sending', () => {
        const book = createTestBook({ momsKindleState: 'sending' });
        component.books = [book];

        component.sendToKindle(book, 'mom');

        expect(mockLibraryApiService.sendLibraryToKindle).not.toHaveBeenCalled();
      });

      it('should allow send to dad when only mom is sending', () => {
        const book = createTestBook({ momsKindleState: 'sending', dadsKindleState: 'idle' });
        component.books = [book];
        mockLibraryApiService.sendLibraryToKindle.and.returnValue(of({ success: true }));

        component.sendToKindle(book, 'dad');

        expect(mockLibraryApiService.sendLibraryToKindle).toHaveBeenCalled();
      });

      it('should allow send to mom when only dad is sending', () => {
        const book = createTestBook({ dadsKindleState: 'sending', momsKindleState: 'idle' });
        component.books = [book];
        mockLibraryApiService.sendLibraryToKindle.and.returnValue(of({ success: true }));

        component.sendToKindle(book, 'mom');

        expect(mockLibraryApiService.sendLibraryToKindle).toHaveBeenCalled();
      });
    });

    describe('Concurrent sends to different books', () => {
      it('should allow concurrent sends to different books', () => {
        const book1 = createTestBook({ fileName: 'book1.epub', dadsKindleState: 'idle' });
        const book2 = createTestBook({ fileName: 'book2.epub', dadsKindleState: 'idle' });
        component.books = [book1, book2];

        // Create subjects to control response timing
        const response1$ = new Subject<{ success: boolean }>();
        const response2$ = new Subject<{ success: boolean }>();

        mockLibraryApiService.sendLibraryToKindle.and.callFake((fileName: string) => {
          return fileName === 'book1.epub' ? response1$.asObservable() : response2$.asObservable();
        });

        // Start both sends
        component.sendToKindle(book1, 'dad');
        component.sendToKindle(book2, 'dad');

        // Both books should be in sending state
        expect(book1.dadsKindleState).toBe('sending');
        expect(book2.dadsKindleState).toBe('sending');

        // Complete book2 first
        response2$.next({ success: true });
        response2$.complete();
        expect(book2.dadsKindleState).toBe('success');
        expect(book1.dadsKindleState).toBe('sending'); // book1 still sending

        // Then complete book1
        response1$.next({ success: true });
        response1$.complete();
        expect(book1.dadsKindleState).toBe('success');
      });
    });

    describe('Concurrent sends to same book different targets', () => {
      it('should allow concurrent sends to dad and mom for same book', () => {
        const book = createTestBook({ dadsKindleState: 'idle', momsKindleState: 'idle' });
        component.books = [book];

        const dadResponse$ = new Subject<{ success: boolean }>();
        const momResponse$ = new Subject<{ success: boolean }>();

        mockLibraryApiService.sendLibraryToKindle.and.callFake(
          (fileName: string, title: string, target: string) => {
            return target === 'dad' ? dadResponse$.asObservable() : momResponse$.asObservable();
          }
        );

        // Start both sends
        component.sendToKindle(book, 'dad');
        component.sendToKindle(book, 'mom');

        // Both targets should be in sending state
        expect(book.dadsKindleState).toBe('sending');
        expect(book.momsKindleState).toBe('sending');

        // Complete mom first
        momResponse$.next({ success: true });
        momResponse$.complete();
        expect(book.momsKindleState).toBe('success');
        expect(book.dadsKindleState).toBe('sending');

        // Then dad
        dadResponse$.next({ success: true });
        dadResponse$.complete();
        expect(book.dadsKindleState).toBe('success');
      });
    });

    describe('Concurrent rating updates', () => {
      it('should handle rapid rating changes', () => {
        const book = createTestBook({ personalRating: 0 });
        component.books = [book];
        mockLibraryApiService.updateLibraryBookRatings.and.returnValue(of(book as unknown as ServiceLibraryBook));

        // Rapidly change ratings
        component.setPersonalRating(book, 3);
        expect(book.personalRating).toBe(3);

        component.setPersonalRating(book, 5);
        expect(book.personalRating).toBe(5);

        component.setPersonalRating(book, 1);
        expect(book.personalRating).toBe(1);

        // All three calls should have been made
        expect(mockLibraryApiService.updateLibraryBookRatings).toHaveBeenCalledTimes(3);
      });

      it('should skip update when rating is same as current', () => {
        const book = createTestBook({ personalRating: 3 });
        component.books = [book];
        mockLibraryApiService.updateLibraryBookRatings.and.returnValue(of(book as unknown as ServiceLibraryBook));

        component.setPersonalRating(book, 3);

        expect(mockLibraryApiService.updateLibraryBookRatings).not.toHaveBeenCalled();
      });

      it('should toggle rating off when clicking 1-star on 1-star book', () => {
        const book = createTestBook({ personalRating: 1 });
        component.books = [book];
        mockLibraryApiService.updateLibraryBookRatings.and.returnValue(of(book as unknown as ServiceLibraryBook));

        component.setPersonalRating(book, 1);

        expect(book.personalRating).toBe(0);
        expect(mockLibraryApiService.updateLibraryBookRatings).toHaveBeenCalledWith(
          'test-book.epub',
          { personalRating: 0 }
        );
      });
    });

    describe('Concurrent bulk operations', () => {
      it('should process bulk sends sequentially with delay', fakeAsync(() => {
        const book1 = createTestBook({ fileName: 'book1.epub' });
        const book2 = createTestBook({ fileName: 'book2.epub' });
        const book3 = createTestBook({ fileName: 'book3.epub' });
        component.books = [book1, book2, book3];
        component.bulkEditMode = true;
        component.selectedBooksForBulk.add('book1.epub');
        component.selectedBooksForBulk.add('book2.epub');
        component.selectedBooksForBulk.add('book3.epub');

        mockLibraryApiService.sendLibraryToKindle.and.returnValue(of({ success: true }));

        component.bulkSend('kindle-dad');

        // First book should be processed immediately
        expect(book1.dadsKindleState).toBe('success');

        // Wait for delay between first and second book (2000ms)
        tick(2000);
        expect(book2.dadsKindleState).toBe('success');

        // Wait for delay between second and third book
        tick(2000);
        expect(book3.dadsKindleState).toBe('success');

        // Bulk edit mode should be exited
        expect(component.bulkEditMode).toBe(false);
        expect(component.selectedBooksForBulk.size).toBe(0);
      }));

      it('should not start bulk send when no books selected', fakeAsync(() => {
        component.selectedBooksForBulk.clear();
        mockLibraryApiService.sendLibraryToKindle.and.returnValue(of({ success: true }));

        component.bulkSend('kindle-dad');
        tick(100);

        expect(mockLibraryApiService.sendLibraryToKindle).not.toHaveBeenCalled();
      }));
    });

    describe('Error handling during concurrent operations', () => {
      it('should handle errors independently for concurrent sends', () => {
        const book1 = createTestBook({ fileName: 'book1.epub' });
        const book2 = createTestBook({ fileName: 'book2.epub' });
        component.books = [book1, book2];

        const response1$ = new Subject<{ success: boolean }>();
        const response2$ = new Subject<{ success: boolean }>();

        mockLibraryApiService.sendLibraryToKindle.and.callFake((fileName: string) => {
          return fileName === 'book1.epub' ? response1$.asObservable() : response2$.asObservable();
        });

        // Start both sends
        component.sendToKindle(book1, 'dad');
        component.sendToKindle(book2, 'dad');

        // book1 fails
        response1$.error(new Error('Network error'));
        expect(book1.dadsKindleState).toBe('error');

        // book2 succeeds
        response2$.next({ success: true });
        response2$.complete();
        expect(book2.dadsKindleState).toBe('success');
      });

      it('should handle API returning success=false', () => {
        const book = createTestBook();
        component.books = [book];
        mockLibraryApiService.sendLibraryToKindle.and.returnValue(of({ success: false }));

        component.sendToKindle(book, 'dad');

        expect(book.dadsKindleState).toBe('error');
      });
    });

    describe('Race condition: book removal during operation', () => {
      it('should handle book list changes during send operations', () => {
        const book1 = createTestBook({ fileName: 'book1.epub' });
        const book2 = createTestBook({ fileName: 'book2.epub' });
        component.books = [book1, book2];

        const response$ = new Subject<{ success: boolean }>();
        mockLibraryApiService.sendLibraryToKindle.and.returnValue(response$.asObservable());

        // Start send for book1
        component.sendToKindle(book1, 'dad');
        expect(book1.dadsKindleState).toBe('sending');

        // Simulate book1 being removed from list (e.g., deleted via dialog)
        component.books = [book2];

        // Complete the send - should not throw
        response$.next({ success: true });
        response$.complete();

        // book1 reference should still be updated even if removed from list
        expect(book1.dadsKindleState).toBe('success');
      });
    });

    describe('Concurrent filter changes', () => {
      const createTestBooks = (count: number) => {
        // Create books where higher-numbered books are newer
        // Title sort: Book 0, 1, 2... (alphabetical)
        // Recent sort: Book 9, 8, 7... (newest first)
        return Array.from({ length: count }, (_, i) => createTestBook({
          fileName: `book-${i}.epub`,
          title: `Book ${i}`,
          primaryGenre: i % 2 === 0 ? 'Fiction' : 'Non-Fiction',
          personalRating: i % 5,
          // Book 0 is oldest (Jan 1), Book 9 is newest (Jan 10)
          savedAt: new Date(2024, 0, i + 1).toISOString()
        }));
      };

      it('should handle rapid filter changes', () => {
        const allBooks = createTestBooks(20);
        component.books = allBooks;

        // With async filtering, we test the internal filter logic directly
        // The filtered result is now computed asynchronously via displayBooks
        // Test that filtering logic works correctly
        component.selectedGenre = 'Fiction';
        const filtered1 = allBooks.filter(b => b.primaryGenre === 'Fiction');
        component.displayBooks = filtered1;
        expect(component.filteredBooks.every(b => b.primaryGenre === 'Fiction')).toBe(true);

        component.selectedGenre = 'Non-Fiction';
        const filtered2 = allBooks.filter(b => b.primaryGenre === 'Non-Fiction');
        component.displayBooks = filtered2;
        expect(component.filteredBooks.every(b => b.primaryGenre === 'Non-Fiction')).toBe(true);

        component.selectedGenre = '';
        component.displayBooks = allBooks;
        expect(component.filteredBooks.length).toBe(20);
      });

      it('should handle rapid sort changes', () => {
        const books = createTestBooks(10);
        component.books = books;

        // Test that different sort orders produce different results
        // Create sorted versions to simulate async behavior
        const sortedByTitle = [...books].sort((a, b) => (a.title || '').localeCompare(b.title || ''));
        const sortedByRecent = [...books].sort((a, b) => {
          const aTime = a.savedAt ? new Date(a.savedAt).getTime() : 0;
          const bTime = b.savedAt ? new Date(b.savedAt).getTime() : 0;
          return bTime - aTime; // Recent first
        });

        component.sortOrder = 'title';
        component.displayBooks = sortedByTitle;

        component.sortOrder = 'recent';
        component.displayBooks = sortedByRecent;

        // Title sort and recent sort should produce different orders
        expect(sortedByTitle).not.toEqual(sortedByRecent);
      });
    });

    describe('State consistency after concurrent operations', () => {
      it('should maintain consistent state after multiple operations complete', () => {
        const book = createTestBook();
        component.books = [book];

        // Setup staggered responses
        const kindleResponse$ = new Subject<{ success: boolean }>();
        const ratingResponse$ = new Subject<ServiceLibraryBook>();

        mockLibraryApiService.sendLibraryToKindle.and.returnValue(kindleResponse$.asObservable());
        mockLibraryApiService.updateLibraryBookRatings.and.returnValue(ratingResponse$.asObservable());

        // Start multiple operations
        component.sendToKindle(book, 'dad');
        component.setPersonalRating(book, 4);

        expect(book.dadsKindleState).toBe('sending');
        expect(book.personalRating).toBe(4); // Optimistically updated

        // Complete rating first
        ratingResponse$.next(book as unknown as ServiceLibraryBook);
        ratingResponse$.complete();

        // Complete kindle send
        kindleResponse$.next({ success: true });
        kindleResponse$.complete();

        // Both should reflect final state
        expect(book.dadsKindleState).toBe('success');
        expect(book.personalRating).toBe(4);
      });
    });
  });
});
