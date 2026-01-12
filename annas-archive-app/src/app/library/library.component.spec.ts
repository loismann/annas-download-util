import { ComponentFixture, TestBed } from '@angular/core/testing';
import { LibraryComponent } from './library.component';
import { AnnaArchiveApiService } from '../services/anna-archive-api.service';
import { MatDialog } from '@angular/material/dialog';
import { AuthService } from '../services/auth.service';
import { of, throwError } from 'rxjs';
import { BookEditDialogResult } from '../components/book-edit-dialog/book-edit-dialog.component';

describe('LibraryComponent', () => {
  let component: LibraryComponent;
  let fixture: ComponentFixture<LibraryComponent>;
  let mockApiService: jasmine.SpyObj<AnnaArchiveApiService>;
  let mockDialog: jasmine.SpyObj<MatDialog>;
  let mockAuthService: jasmine.SpyObj<AuthService>;

  beforeEach(async () => {
    mockApiService = jasmine.createSpyObj('AnnaArchiveApiService', [
      'getLibraryBooks',
      'updateLibraryBookMetadata',
      'updateLibraryBookCover',
      'updateLibraryBookRatings',
      'deleteLibraryBook',
      'sendLibraryToKindle',
      'wipeLibraryGenres',
      'updateLibraryBookReaderEnabled'
    ]);

    mockDialog = jasmine.createSpyObj('MatDialog', ['open']);
    mockAuthService = jasmine.createSpyObj('AuthService', ['isAdmin']);

    // Default mock returns
    mockApiService.getLibraryBooks.and.returnValue(of([]));
    mockAuthService.isAdmin.and.returnValue(true);

    await TestBed.configureTestingModule({
      imports: [LibraryComponent],
      providers: [
        { provide: AnnaArchiveApiService, useValue: mockApiService },
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
      mockApiService.updateLibraryBookMetadata.and.returnValue(of({}));
      mockApiService.updateLibraryBookCover.and.returnValue(
        of({ coverUrl: newCoverUrl })
      );

      // Act
      component.onCoverClick(testBook);

      // Assert - Cover update should be called
      setTimeout(() => {
        expect(mockApiService.updateLibraryBookCover).toHaveBeenCalledWith(
          'test-book.epub',
          newCoverUrl
        );

        // Book's coverUrl should be updated with cache-busting timestamp
        expect(testBook.coverUrl).toContain(newCoverUrl);
        expect(testBook.coverUrl).toContain('?t=');
        done();
      }, 50);
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
      mockApiService.updateLibraryBookMetadata.and.returnValue(of({}));

      // Act
      component.onCoverClick(testBook);

      // Assert - Cover update should NOT be called
      setTimeout(() => {
        expect(mockApiService.updateLibraryBookCover).not.toHaveBeenCalled();
        done();
      }, 50);
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
      mockApiService.updateLibraryBookMetadata.and.returnValue(of({}));
      mockApiService.updateLibraryBookCover.and.returnValue(
        throwError(() => new Error('API Error'))
      );

      spyOn(console, 'error');

      // Act
      component.onCoverClick(testBook);

      // Assert - Error should be logged, but component should not crash
      setTimeout(() => {
        expect(console.error).toHaveBeenCalled();
        expect(testBook.coverUrl).toBe('http://example.com/old-cover.jpg'); // Unchanged
        done();
      }, 50);
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
      mockApiService.updateLibraryBookMetadata.and.returnValue(of({}));
      mockApiService.updateLibraryBookCover.and.returnValue(
        of({ coverUrl: newCoverUrl })
      );

      // Act
      component.onCoverClick(testBook);

      // Assert - Should use & instead of ? for timestamp
      setTimeout(() => {
        expect(testBook.coverUrl).toContain(newCoverUrl);
        expect(testBook.coverUrl).toContain('&t=');
        expect(testBook.coverUrl).not.toContain('?t=');
        done();
      }, 50);
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

      mockApiService.getLibraryBooks.and.returnValue(of(testBooks));

      // Act - trigger ngOnInit which loads books and builds genre list
      component.ngOnInit();

      // Assert - owner tags should NOT be in the regular genre list
      expect(component.genres).not.toContain("Dad's Books");
      expect(component.genres).not.toContain("Mom's Books");
      expect(component.genres).not.toContain("Paul's Books");
      expect(component.genres).toContain('Fantasy');
    });
  });
});
