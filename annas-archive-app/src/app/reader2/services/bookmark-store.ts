import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Reader2ApiService } from './reader2-api.service';
import { ReaderTasks } from './reader-tasks';
import { Bookmark } from '../reader2.models';

/**
 * The reader's marks in the book they have open.
 *
 * <p>Its own store rather than more fields on {@link ReaderStore}: bookmarks are
 * a list the reader edits, where reading state is a position that follows them
 * around. Folding them together is how Reader I's single component reached 2,283
 * lines — every feature was one more field on the same object until nothing
 * could be changed in isolation.</p>
 *
 * <p>Nothing here spends money. Marking a place is a database row.</p>
 */
@Injectable()
export class BookmarkStore {
  private readonly api = inject(Reader2ApiService);
  private readonly tasks = inject(ReaderTasks);

  readonly bookmarks = signal<Bookmark[]>([]);

  private readonly bookId = signal<string | null>(null);

  /** Where the reader is now, so the toggle knows which way it points. */
  private readonly here = signal<Place>({ chapter: -1, wordOffset: -1 });

  /**
   * The mark at the reader's current place, if there is one.
   *
   * <p>Derived rather than a flag the caller sets: a flag would have to be
   * cleared on page turn, chapter change, and delete, and missing one of those
   * leaves the toggle claiming a page is marked when it is not.</p>
   */
  readonly markHere = computed<Bookmark | null>(() => {
    const { chapter, wordOffset } = this.here();
    return this.bookmarks().find(b => b.chapter === chapter && b.wordOffset === wordOffset) ?? null;
  });

  /** Called by the shell whenever the reader moves. */
  setPlace(chapter: number, wordOffset: number): void {
    this.here.set({ chapter, wordOffset });
  }

  async loadAsync(bookId: string): Promise<void> {
    this.bookId.set(bookId);
    this.bookmarks.set([]);

    const loaded = await this.tasks.run(
      'Loading your bookmarks', () => firstValueFrom(this.api.bookmarks(bookId)));

    if (loaded) this.bookmarks.set(loaded);
  }

  /**
   * Marks the current place, or removes the mark already there.
   *
   * <p>One method rather than add and remove, because the control is one button
   * and splitting it would leave the decision of which to call in the template.
   * </p>
   */
  async toggleAsync(label: string | null = null): Promise<void> {
    const existing = this.markHere();

    if (existing) {
      await this.removeAsync(existing.id);
      return;
    }

    const { chapter, wordOffset } = this.here();
    await this.saveAsync(chapter, wordOffset, label);
  }

  async saveAsync(chapter: number, wordOffset: number, label: string | null): Promise<void> {
    const bookId = this.bookId();
    if (!bookId || chapter < 0 || wordOffset < 0) return;

    const saved = await this.tasks.run(
      'Saving the bookmark',
      () => firstValueFrom(this.api.saveBookmark(bookId, chapter, wordOffset, label)));

    if (saved) this.bookmarks.set(inReadingOrder([...this.without(saved.id), saved]));
  }

  async removeAsync(bookmarkId: string): Promise<void> {
    const bookId = this.bookId();
    if (!bookId) return;

    const removed = await this.tasks.run(
      'Removing the bookmark',
      async () => { await firstValueFrom(this.api.removeBookmark(bookId, bookmarkId)); return true; });

    if (removed) this.bookmarks.set(this.without(bookmarkId));
  }

  private without(bookmarkId: string): Bookmark[] {
    return this.bookmarks().filter(b => b.id !== bookmarkId);
  }
}

interface Place {
  chapter: number;
  wordOffset: number;
}

/**
 * The order the server returns them in, kept locally so an added mark lands in
 * the right place without a round trip to find out where.
 */
function inReadingOrder(bookmarks: Bookmark[]): Bookmark[] {
  return [...bookmarks].sort((a, b) => a.chapter - b.chapter || a.wordOffset - b.wordOffset);
}
