import { Injectable } from '@angular/core';
import { LoggerService } from '../../services/logger.service';

/**
 * Represents a book that has been viewed in the reader.
 */
export interface ViewedBook {
  fileName: string;
  readerKey: string;
  title: string;
  updatedAt?: string;
}

/**
 * Represents a bookmark entry for a specific position in a book.
 */
export interface BookmarkEntry {
  id: string;
  readerKey: string;
  chapterId: number;
  wordOffset: number;
  createdAt: string;
}

/**
 * Represents a reading position within a book.
 */
export interface ReadingPosition {
  chapterId: number;
  wordOffset: number;
  updatedAt?: string;
}

const STORAGE_KEYS = {
  RECENT: 'epub_recent',
  BOOKMARKS: 'epub_bookmarks',
  LAST_POSITIONS: 'epub_last_positions'
} as const;

const MAX_RECENT_BOOKS = 8;

/**
 * Service for managing reader state including reading history,
 * bookmarks, and last reading positions.
 * Handles localStorage persistence for all state.
 */
@Injectable({
  providedIn: 'root'
})
export class ReaderStateService {
  private previouslyViewed: ViewedBook[] = [];
  private bookmarks: BookmarkEntry[] = [];
  private lastPositions = new Map<string, ReadingPosition>();

  constructor(private logger: LoggerService) {
    this.loadAll();
  }

  /**
   * Loads all state from localStorage.
   */
  private loadAll(): void {
    this.loadPreviouslyViewed();
    this.loadBookmarks();
    this.loadLastPositions();
  }

  // ─── Previously Viewed Books ────────────────────────────────────────────────

  /**
   * Gets the list of previously viewed books.
   */
  getPreviouslyViewed(): ViewedBook[] {
    return [...this.previouslyViewed];
  }

  /**
   * Records a book as viewed, moving it to the top of the list.
   */
  recordViewed(book: { fileName: string; readerKey: string; title: string }): void {
    if (!this.isLocalStorageAvailable()) return;

    const entry: ViewedBook = {
      fileName: book.fileName,
      readerKey: book.readerKey,
      title: book.title,
      updatedAt: new Date().toISOString()
    };

    this.previouslyViewed = [
      entry,
      ...this.previouslyViewed.filter(b => b.readerKey !== book.readerKey)
    ].slice(0, MAX_RECENT_BOOKS);

    this.persistPreviouslyViewed();
  }

  /**
   * Removes a book from the previously viewed list.
   */
  removePreviouslyViewedEntry(fileName: string): void {
    const next = this.previouslyViewed.filter(entry => entry.fileName !== fileName);
    if (next.length === this.previouslyViewed.length) return;
    this.previouslyViewed = next;
    this.persistPreviouslyViewed();
  }

  /**
   * Clears all previously viewed entries.
   */
  clearPreviouslyViewed(): void {
    if (!this.isLocalStorageAvailable()) return;
    this.previouslyViewed = [];
    localStorage.removeItem(STORAGE_KEYS.RECENT);
  }

  /**
   * Reconciles previously viewed list with available books.
   * Removes entries for books that are no longer in the reader.
   */
  reconcilePreviouslyViewed(availableFileNames: Set<string>): void {
    if (!availableFileNames.size) return;
    const filtered = this.previouslyViewed.filter(entry => availableFileNames.has(entry.fileName));
    if (filtered.length === this.previouslyViewed.length) return;
    this.previouslyViewed = filtered;
    this.persistPreviouslyViewed();
  }

  private loadPreviouslyViewed(): void {
    if (!this.isLocalStorageAvailable()) return;

    try {
      const raw = localStorage.getItem(STORAGE_KEYS.RECENT);
      if (!raw) return;
      const parsed: unknown = JSON.parse(raw);
      if (!Array.isArray(parsed)) {
        this.previouslyViewed = [];
        return;
      }

      this.previouslyViewed = parsed.map((item: unknown) => {
        if (typeof item === 'object' && item !== null) {
          const obj = item as Record<string, unknown>;
          if ('fileName' in obj && 'readerKey' in obj) {
            return obj as unknown as ViewedBook;
          }
          // Handle legacy format
          return {
            fileName: (obj['path'] as string) ?? '',
            readerKey: (obj['path'] as string) ?? '',
            title: (obj['name'] as string) ?? (obj['path'] as string) ?? 'Unknown',
            updatedAt: (obj['serverModified'] as string) ?? undefined
          } as ViewedBook;
        }
        return null;
      }).filter((entry): entry is ViewedBook => entry !== null && !!entry.fileName && !!entry.readerKey);
    } catch {
      this.previouslyViewed = [];
    }
  }

  private persistPreviouslyViewed(): void {
    if (!this.isLocalStorageAvailable()) return;
    localStorage.setItem(STORAGE_KEYS.RECENT, JSON.stringify(this.previouslyViewed));
  }

  // ─── Bookmarks ──────────────────────────────────────────────────────────────

  /**
   * Gets all bookmarks.
   */
  getBookmarks(): BookmarkEntry[] {
    return [...this.bookmarks];
  }

  /**
   * Gets bookmarks for a specific book.
   */
  getBookmarksForBook(readerKey: string): BookmarkEntry[] {
    return this.bookmarks.filter(b => b.readerKey === readerKey);
  }

  /**
   * Checks if a specific position is bookmarked.
   */
  isBookmarked(readerKey: string, chapterId: number, wordOffset: number): boolean {
    return this.bookmarks.some(
      b => b.readerKey === readerKey && b.chapterId === chapterId && b.wordOffset === wordOffset
    );
  }

  /**
   * Adds a bookmark at the current position.
   */
  addBookmark(readerKey: string, chapterId: number, wordOffset: number): BookmarkEntry {
    const bookmark: BookmarkEntry = {
      id: `${readerKey}:${chapterId}:${wordOffset}`,
      readerKey,
      chapterId,
      wordOffset,
      createdAt: new Date().toISOString()
    };
    this.bookmarks.push(bookmark);
    this.saveBookmarks();
    return bookmark;
  }

  /**
   * Removes a bookmark by ID.
   */
  removeBookmark(bookmarkId: string): void {
    this.bookmarks = this.bookmarks.filter(b => b.id !== bookmarkId);
    this.saveBookmarks();
  }

  /**
   * Removes a bookmark at a specific position.
   */
  removeBookmarkAtPosition(readerKey: string, chapterId: number, wordOffset: number): void {
    const id = `${readerKey}:${chapterId}:${wordOffset}`;
    this.removeBookmark(id);
  }

  /**
   * Toggles a bookmark at the current position.
   * Returns the bookmark if created, null if removed.
   */
  toggleBookmark(readerKey: string, chapterId: number, wordOffset: number): BookmarkEntry | null {
    if (this.isBookmarked(readerKey, chapterId, wordOffset)) {
      this.removeBookmarkAtPosition(readerKey, chapterId, wordOffset);
      return null;
    }
    return this.addBookmark(readerKey, chapterId, wordOffset);
  }

  /**
   * Gets a bookmark by ID.
   */
  getBookmarkById(bookmarkId: string): BookmarkEntry | undefined {
    return this.bookmarks.find(b => b.id === bookmarkId);
  }

  private loadBookmarks(): void {
    if (!this.isLocalStorageAvailable()) return;
    try {
      const raw = localStorage.getItem(STORAGE_KEYS.BOOKMARKS);
      if (!raw) {
        this.bookmarks = [];
        return;
      }
      const parsed = JSON.parse(raw) as BookmarkEntry[];
      this.bookmarks = Array.isArray(parsed) ? parsed : [];
    } catch {
      this.bookmarks = [];
    }
  }

  private saveBookmarks(): void {
    if (!this.isLocalStorageAvailable()) return;
    localStorage.setItem(STORAGE_KEYS.BOOKMARKS, JSON.stringify(this.bookmarks));
  }

  // ─── Last Positions ─────────────────────────────────────────────────────────

  /**
   * Gets the last reading position for a book.
   */
  getLastPosition(readerKey: string): ReadingPosition | undefined {
    return this.lastPositions.get(readerKey);
  }

  /**
   * Updates the reading position for a book.
   */
  updateReadingPosition(readerKey: string, chapterId: number, wordOffset: number): void {
    const entry: ReadingPosition = {
      chapterId,
      wordOffset,
      updatedAt: new Date().toISOString()
    };
    this.lastPositions.set(readerKey, entry);
    this.persistLastPositions();
  }

  /**
   * Removes the last position for a book.
   */
  removeLastPosition(readerKey: string): void {
    this.lastPositions.delete(readerKey);
    this.persistLastPositions();
  }

  private loadLastPositions(): void {
    if (!this.isLocalStorageAvailable()) return;
    try {
      const raw = localStorage.getItem(STORAGE_KEYS.LAST_POSITIONS);
      if (!raw) return;
      const parsed = JSON.parse(raw) as Record<string, ReadingPosition>;
      if (!parsed || typeof parsed !== 'object') return;
      Object.entries(parsed).forEach(([key, value]) => {
        if (value && typeof value.chapterId === 'number' && typeof value.wordOffset === 'number') {
          this.lastPositions.set(key, value);
        }
      });
    } catch {
      this.lastPositions.clear();
    }
  }

  private persistLastPositions(): void {
    if (!this.isLocalStorageAvailable()) return;
    const payload: Record<string, ReadingPosition> = {};
    this.lastPositions.forEach((value, key) => {
      payload[key] = value;
    });
    localStorage.setItem(STORAGE_KEYS.LAST_POSITIONS, JSON.stringify(payload));
  }

  // ─── Utilities ──────────────────────────────────────────────────────────────

  private isLocalStorageAvailable(): boolean {
    return typeof localStorage !== 'undefined';
  }
}
