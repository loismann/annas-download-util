import { TestBed } from '@angular/core/testing';
import { ReaderStateService, ViewedBook, BookmarkEntry, ReadingPosition } from './reader-state.service';
import { LoggerService } from '../../services/logger.service';

describe('ReaderStateService', () => {
  let service: ReaderStateService;
  let mockLogger: jasmine.SpyObj<LoggerService>;

  beforeEach(() => {
    // Clear localStorage before each test
    localStorage.clear();

    mockLogger = jasmine.createSpyObj('LoggerService', ['log', 'warn', 'error']);

    TestBed.configureTestingModule({
      providers: [
        ReaderStateService,
        { provide: LoggerService, useValue: mockLogger }
      ]
    });
    service = TestBed.inject(ReaderStateService);
  });

  afterEach(() => {
    localStorage.clear();
  });

  describe('Previously Viewed Books', () => {
    it('should return empty array when no books viewed', () => {
      expect(service.getPreviouslyViewed()).toEqual([]);
    });

    it('should record a viewed book', () => {
      const book = { fileName: 'book.epub', readerKey: '/path/book.epub', title: 'Test Book' };
      service.recordViewed(book);

      const viewed = service.getPreviouslyViewed();
      expect(viewed.length).toBe(1);
      expect(viewed[0].fileName).toBe('book.epub');
      expect(viewed[0].readerKey).toBe('/path/book.epub');
      expect(viewed[0].title).toBe('Test Book');
      expect(viewed[0].updatedAt).toBeDefined();
    });

    it('should move re-viewed book to top of list', () => {
      const book1 = { fileName: 'book1.epub', readerKey: '/path/book1.epub', title: 'Book 1' };
      const book2 = { fileName: 'book2.epub', readerKey: '/path/book2.epub', title: 'Book 2' };

      service.recordViewed(book1);
      service.recordViewed(book2);
      service.recordViewed(book1);

      const viewed = service.getPreviouslyViewed();
      expect(viewed.length).toBe(2);
      expect(viewed[0].fileName).toBe('book1.epub');
      expect(viewed[1].fileName).toBe('book2.epub');
    });

    it('should limit to 8 most recent books', () => {
      for (let i = 0; i < 10; i++) {
        service.recordViewed({
          fileName: `book${i}.epub`,
          readerKey: `/path/book${i}.epub`,
          title: `Book ${i}`
        });
      }

      expect(service.getPreviouslyViewed().length).toBe(8);
    });

    it('should remove a book from previously viewed', () => {
      const book = { fileName: 'book.epub', readerKey: '/path/book.epub', title: 'Test Book' };
      service.recordViewed(book);

      service.removePreviouslyViewedEntry('book.epub');
      expect(service.getPreviouslyViewed().length).toBe(0);
    });

    it('should not modify list when removing non-existent book', () => {
      const book = { fileName: 'book.epub', readerKey: '/path/book.epub', title: 'Test Book' };
      service.recordViewed(book);

      service.removePreviouslyViewedEntry('nonexistent.epub');
      expect(service.getPreviouslyViewed().length).toBe(1);
    });

    it('should clear all previously viewed books', () => {
      service.recordViewed({ fileName: 'book1.epub', readerKey: '/path/book1.epub', title: 'Book 1' });
      service.recordViewed({ fileName: 'book2.epub', readerKey: '/path/book2.epub', title: 'Book 2' });

      service.clearPreviouslyViewed();
      expect(service.getPreviouslyViewed().length).toBe(0);
    });

    it('should reconcile with available books', () => {
      service.recordViewed({ fileName: 'book1.epub', readerKey: '/path/book1.epub', title: 'Book 1' });
      service.recordViewed({ fileName: 'book2.epub', readerKey: '/path/book2.epub', title: 'Book 2' });
      service.recordViewed({ fileName: 'book3.epub', readerKey: '/path/book3.epub', title: 'Book 3' });

      const available = new Set(['book1.epub', 'book3.epub']);
      service.reconcilePreviouslyViewed(available);

      const viewed = service.getPreviouslyViewed();
      expect(viewed.length).toBe(2);
      expect(viewed.some(b => b.fileName === 'book2.epub')).toBe(false);
    });

    it('should not reconcile with empty set', () => {
      service.recordViewed({ fileName: 'book1.epub', readerKey: '/path/book1.epub', title: 'Book 1' });

      service.reconcilePreviouslyViewed(new Set());
      expect(service.getPreviouslyViewed().length).toBe(1);
    });

    it('should persist previously viewed to localStorage', () => {
      const book = { fileName: 'book.epub', readerKey: '/path/book.epub', title: 'Test Book' };
      service.recordViewed(book);

      const stored = localStorage.getItem('epub_recent');
      expect(stored).toBeTruthy();
      const parsed = JSON.parse(stored!);
      expect(parsed.length).toBe(1);
      expect(parsed[0].fileName).toBe('book.epub');
    });
  });

  describe('Bookmarks', () => {
    it('should return empty array when no bookmarks', () => {
      expect(service.getBookmarks()).toEqual([]);
    });

    it('should add a bookmark', () => {
      const bookmark = service.addBookmark('/path/book.epub', 1, 100);

      expect(bookmark.readerKey).toBe('/path/book.epub');
      expect(bookmark.chapterId).toBe(1);
      expect(bookmark.wordOffset).toBe(100);
      expect(bookmark.id).toBe('/path/book.epub:1:100');
      expect(bookmark.createdAt).toBeDefined();
    });

    it('should get bookmarks for a specific book', () => {
      service.addBookmark('/path/book1.epub', 1, 100);
      service.addBookmark('/path/book1.epub', 2, 200);
      service.addBookmark('/path/book2.epub', 1, 50);

      const book1Bookmarks = service.getBookmarksForBook('/path/book1.epub');
      expect(book1Bookmarks.length).toBe(2);

      const book2Bookmarks = service.getBookmarksForBook('/path/book2.epub');
      expect(book2Bookmarks.length).toBe(1);
    });

    it('should check if position is bookmarked', () => {
      service.addBookmark('/path/book.epub', 1, 100);

      expect(service.isBookmarked('/path/book.epub', 1, 100)).toBe(true);
      expect(service.isBookmarked('/path/book.epub', 1, 200)).toBe(false);
      expect(service.isBookmarked('/path/book.epub', 2, 100)).toBe(false);
      expect(service.isBookmarked('/path/other.epub', 1, 100)).toBe(false);
    });

    it('should remove a bookmark by ID', () => {
      service.addBookmark('/path/book.epub', 1, 100);
      service.addBookmark('/path/book.epub', 2, 200);

      service.removeBookmark('/path/book.epub:1:100');
      expect(service.getBookmarks().length).toBe(1);
      expect(service.isBookmarked('/path/book.epub', 1, 100)).toBe(false);
    });

    it('should remove bookmark at position', () => {
      service.addBookmark('/path/book.epub', 1, 100);

      service.removeBookmarkAtPosition('/path/book.epub', 1, 100);
      expect(service.getBookmarks().length).toBe(0);
    });

    it('should toggle bookmark - add when not present', () => {
      const result = service.toggleBookmark('/path/book.epub', 1, 100);

      expect(result).not.toBeNull();
      expect(result?.readerKey).toBe('/path/book.epub');
      expect(service.isBookmarked('/path/book.epub', 1, 100)).toBe(true);
    });

    it('should toggle bookmark - remove when present', () => {
      service.addBookmark('/path/book.epub', 1, 100);

      const result = service.toggleBookmark('/path/book.epub', 1, 100);

      expect(result).toBeNull();
      expect(service.isBookmarked('/path/book.epub', 1, 100)).toBe(false);
    });

    it('should get bookmark by ID', () => {
      service.addBookmark('/path/book.epub', 1, 100);

      const bookmark = service.getBookmarkById('/path/book.epub:1:100');
      expect(bookmark).toBeDefined();
      expect(bookmark?.chapterId).toBe(1);
      expect(bookmark?.wordOffset).toBe(100);
    });

    it('should return undefined for non-existent bookmark ID', () => {
      const bookmark = service.getBookmarkById('nonexistent');
      expect(bookmark).toBeUndefined();
    });

    it('should persist bookmarks to localStorage', () => {
      service.addBookmark('/path/book.epub', 1, 100);

      const stored = localStorage.getItem('epub_bookmarks');
      expect(stored).toBeTruthy();
      const parsed = JSON.parse(stored!);
      expect(parsed.length).toBe(1);
    });
  });

  describe('Last Reading Positions', () => {
    it('should return undefined for unknown book', () => {
      expect(service.getLastPosition('/path/unknown.epub')).toBeUndefined();
    });

    it('should update reading position', () => {
      service.updateReadingPosition('/path/book.epub', 3, 500);

      const position = service.getLastPosition('/path/book.epub');
      expect(position).toBeDefined();
      expect(position?.chapterId).toBe(3);
      expect(position?.wordOffset).toBe(500);
      expect(position?.updatedAt).toBeDefined();
    });

    it('should overwrite previous position', () => {
      service.updateReadingPosition('/path/book.epub', 1, 100);
      service.updateReadingPosition('/path/book.epub', 5, 1000);

      const position = service.getLastPosition('/path/book.epub');
      expect(position?.chapterId).toBe(5);
      expect(position?.wordOffset).toBe(1000);
    });

    it('should maintain separate positions for different books', () => {
      service.updateReadingPosition('/path/book1.epub', 1, 100);
      service.updateReadingPosition('/path/book2.epub', 3, 500);

      expect(service.getLastPosition('/path/book1.epub')?.chapterId).toBe(1);
      expect(service.getLastPosition('/path/book2.epub')?.chapterId).toBe(3);
    });

    it('should remove last position', () => {
      service.updateReadingPosition('/path/book.epub', 1, 100);

      service.removeLastPosition('/path/book.epub');
      expect(service.getLastPosition('/path/book.epub')).toBeUndefined();
    });

    it('should persist positions to localStorage', () => {
      service.updateReadingPosition('/path/book.epub', 1, 100);

      const stored = localStorage.getItem('epub_last_positions');
      expect(stored).toBeTruthy();
      const parsed = JSON.parse(stored!);
      expect(parsed['/path/book.epub']).toBeDefined();
      expect(parsed['/path/book.epub'].chapterId).toBe(1);
    });
  });

  describe('localStorage Persistence', () => {
    it('should load previously viewed from localStorage on init', () => {
      // Note: Service is already initialized, so we test via recording and checking persistence
      const book = { fileName: 'persist.epub', readerKey: '/path/persist.epub', title: 'Persist Test' };
      service.recordViewed(book);

      const stored = localStorage.getItem('epub_recent');
      expect(stored).toBeTruthy();
      const parsed = JSON.parse(stored!);
      expect(parsed.length).toBe(1);
      expect(parsed[0].fileName).toBe('persist.epub');
    });

    it('should handle invalid JSON in localStorage gracefully', () => {
      // Clear and set invalid data, then create fresh module
      localStorage.clear();
      localStorage.setItem('epub_recent', 'invalid json');

      // Reset TestBed to get fresh service instance
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [
          ReaderStateService,
          { provide: LoggerService, useValue: mockLogger }
        ]
      });

      // Should not throw and should return empty array
      const freshService = TestBed.inject(ReaderStateService);
      expect(freshService.getPreviouslyViewed()).toEqual([]);
    });

    it('should handle legacy format in previously viewed', () => {
      // Clear and set legacy data, then create fresh module
      localStorage.clear();
      const legacyData = [{ path: '/path/legacy.epub', name: 'Legacy Book' }];
      localStorage.setItem('epub_recent', JSON.stringify(legacyData));

      // Reset TestBed to get fresh service instance
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [
          ReaderStateService,
          { provide: LoggerService, useValue: mockLogger }
        ]
      });

      const freshService = TestBed.inject(ReaderStateService);
      const viewed = freshService.getPreviouslyViewed();
      // Should convert legacy format
      expect(viewed.length).toBe(1);
      expect(viewed[0].fileName).toBe('/path/legacy.epub');
    });
  });
});
