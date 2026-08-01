import { Injectable, OnDestroy } from '@angular/core';
import { Subject, takeUntil } from 'rxjs';

import { BookSearchApiService } from '../services/book-search-api.service';
import { BookDto } from '../models/book-dto.model';
import { AUTO_COVER_FETCH_LIMIT, COVER_LOOKUP_STAGGER_MS } from '../constants';

/** The minimum a book needs for an AI-result cover lookup — AI suggestions have
 *  no md5 yet, so they can only be looked up by title/author. */
export interface AiCoverTarget {
  title: string;
  author?: string;
  coverUrl?: string;
}

/**
 * Fills in missing book covers, staggered and best-effort.
 *
 * Split out of BookSearchComponent, which had this interleaved with search,
 * grouping, descriptions and send-to-device. It is a service rather than more
 * component methods for one concrete reason: it owns `setTimeout` handles and
 * long-lived subscriptions, and putting them behind an `ngOnDestroy` that
 * actually cancels them is the whole point. Provide it at component level
 * (`providers: [BookCoverLookupService]`) so its lifetime matches the page.
 *
 * Before this existed, the timers outlived the component. In tests that meant a
 * stray callback firing after the fixture was destroyed and throwing
 * `undefined.subscribe` inside whatever unrelated spec happened to be running.
 */
@Injectable()
export class BookCoverLookupService implements OnDestroy {
  private readonly inFlight = new Set<string>();

  /** Staggers cover lookups triggered by broken images (onCoverError) —
   *  without this, the grid view can have a dozen-plus cards' images fail
   *  within the same animation frame (many more cards visible at once than
   *  the old one-per-row list), each firing its own fallback lookup
   *  immediately. That burst of simultaneous requests to Anna's
   *  Archive/OpenLibrary/Google Books is what was making search feel like
   *  it hung — later requests queue up behind earlier ones and each one
   *  individually gets slower as the pile grows. */
  private readonly queue: BookDto[] = [];
  private pumping = false;

  private readonly timers = new Set<ReturnType<typeof setTimeout>>();
  private readonly destroy$ = new Subject<void>();

  constructor(private readonly api: BookSearchApiService) {}

  ngOnDestroy(): void {
    this.timers.forEach(clearTimeout);
    this.timers.clear();
    this.queue.length = 0;
    this.pumping = false;
    this.destroy$.next();
    this.destroy$.complete();
  }

  /** Queues lookups for whichever of `books` is still missing a usable cover. */
  queueForBooks(books: BookDto[], useLibGen: boolean): void {
    books
      .filter(book => this.needsExternalCoverLookup(book, useLibGen))
      .slice(0, AUTO_COVER_FETCH_LIMIT)
      .forEach(book => this.enqueue(book, useLibGen));
  }

  /** Adds one book to the staggered queue rather than firing immediately. */
  enqueue(book: BookDto, useLibGen: boolean): void {
    if (this.queue.includes(book) || this.inFlight.has(book.md5)) return;
    this.queue.push(book);
    this.pump(useLibGen);
  }

  /**
   * Staggered, best-effort cover fetch for AI Book Search results — these are
   * AI-suggested titles with no MD5 yet (nothing's been matched to a real
   * download), so this uses the title/author lookup rather than the MD5-based
   * one. Failures are silent; a missing cover just stays a placeholder, same as
   * everywhere else covers are optional.
   */
  queueAiResults(results: AiCoverTarget[]): void {
    results.forEach((book, index) => {
      if (book.coverUrl) return;
      this.after(index * COVER_LOOKUP_STAGGER_MS, () => {
        this.api.fetchCover(book.title, book.author)
          .pipe(takeUntil(this.destroy$))
          .subscribe({
            next: resp => {
              if (resp.coverUrl) book.coverUrl = resp.coverUrl;
            },
            error: () => { /* no-op — placeholder stays */ }
          });
      });
    });
  }

  needsExternalCoverLookup(book: BookDto, useLibGen: boolean): boolean {
    if (!book.coverCandidates || book.coverCandidates.length === 0) {
      return true;
    }

    if (!useLibGen && !book.source?.startsWith('libgen')) {
      return false;
    }

    return !book.coverCandidates.some(url => !this.isLibGenCoverUrl(url));
  }

  private isLibGenCoverUrl(url: string): boolean {
    const normalized = url.toLowerCase();
    return normalized.includes('libgen.') && normalized.includes('/covers');
  }

  private pump(useLibGen: boolean): void {
    if (this.pumping) return;
    const next = this.queue.shift();
    if (!next) return;

    this.pumping = true;
    this.lookup(next, useLibGen);
    this.after(COVER_LOOKUP_STAGGER_MS, () => {
      this.pumping = false;
      this.pump(useLibGen);
    });
  }

  private lookup(book: BookDto, useLibGen: boolean): void {
    if (!this.needsExternalCoverLookup(book, useLibGen)) return;
    if (this.inFlight.has(book.md5)) return;

    this.inFlight.add(book.md5);
    const author = book.authors?.[0];

    const addCoverAndFinish = (coverUrl: string) => {
      if (!book.coverCandidates) book.coverCandidates = [];
      book.coverCandidates.unshift(coverUrl);
      this.inFlight.delete(book.md5);
    };

    const fallbackToTitleSearch = () => {
      this.api.fetchCover(book.title, author)
        .pipe(takeUntil(this.destroy$))
        .subscribe({
          next: resp => resp.coverUrl ? addCoverAndFinish(resp.coverUrl) : this.inFlight.delete(book.md5),
          error: () => this.inFlight.delete(book.md5)
        });
    };

    // Try MD5-based ISBN lookup first — independent of OpenLibrary's search
    // API and Google Books' quota, so it works even when those don't. Falls
    // back to the title/author search only if this doesn't find anything.
    this.api.fetchCoverByMd5(book.md5)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: resp => resp.coverUrl ? addCoverAndFinish(resp.coverUrl) : fallbackToTitleSearch(),
        error: () => fallbackToTitleSearch()
      });
  }

  /** setTimeout that forgets its own handle on completion, so `timers` tracks
   *  exactly the pending ones and ngOnDestroy can cancel all of them. */
  private after(ms: number, fn: () => void): void {
    const handle = setTimeout(() => {
      this.timers.delete(handle);
      fn();
    }, ms);
    this.timers.add(handle);
  }
}
