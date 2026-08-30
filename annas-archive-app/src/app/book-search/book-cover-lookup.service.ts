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
  queueForBooks(books: BookDto[]): void {
    books
      .filter(book => this.needsExternalCoverLookup(book))
      .slice(0, AUTO_COVER_FETCH_LIMIT)
      .forEach(book => this.enqueue(book));
  }

  /** Adds one book to the staggered queue rather than firing immediately. */
  enqueue(book: BookDto): void {
    if (this.queue.includes(book) || this.inFlight.has(book.md5)) return;
    this.queue.push(book);
    this.pump();
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

  /**
   * The `useLibGen` argument is gone because the question it answered is gone:
   * there is one catalogue now, and whether a book needs a cover fetched has
   * always been a property of the book's own candidates rather than of which
   * source the reader picked. A LibGen cover URL is a placeholder often enough
   * that a book carrying nothing else still needs a real one looked up.
   */
  needsExternalCoverLookup(book: BookDto): boolean {
    if (!book.coverCandidates || book.coverCandidates.length === 0) {
      return true;
    }

    return !book.coverCandidates.some(url => !this.isLibGenCoverUrl(url));
  }

  /**
   * Both spellings, and the second one is a bug fix: fiction results carry
   * `/fictioncovers/` URLs, which does not contain `/covers` — the slash lands
   * before `fiction`, not before `covers`. So a fiction book whose only
   * candidate was a LibGen cover looked like a book that already had a real
   * cover, and no lookup was ever queued for it.
   */
  private isLibGenCoverUrl(url: string): boolean {
    const normalized = url.toLowerCase();
    return normalized.includes('libgen.')
      && (normalized.includes('/covers') || normalized.includes('/fictioncovers'));
  }

  private pump(): void {
    if (this.pumping) return;
    const next = this.queue.shift();
    if (!next) return;

    this.pumping = true;
    this.lookup(next);
    this.after(COVER_LOOKUP_STAGGER_MS, () => {
      this.pumping = false;
      this.pump();
    });
  }

  private lookup(book: BookDto): void {
    if (!this.needsExternalCoverLookup(book)) return;
    if (this.inFlight.has(book.md5)) return;

    this.inFlight.add(book.md5);
    const author = book.authors?.[0];

    const addCoverAndFinish = (coverUrl: string) => {
      if (!book.coverCandidates) book.coverCandidates = [];
      book.coverCandidates.unshift(coverUrl);
      this.inFlight.delete(book.md5);
    };

    // Straight to the title/author search. There used to be an md5-first rung
    // in front of this, on the grounds that an ISBN taken from the book's own
    // detail page beats a title match — true, and it stopped being available:
    // that page is Anna's Archive HTML, which went behind DDoS-Guard on
    // 2026-08-13. It could no longer answer, and because it reaches the site
    // through Playwright it could not even fail quickly, spending up to thirty
    // seconds per book behind one shared browser lock before the rung below was
    // reached at all. Covers did not disappear because there was no source —
    // the working source was second in a queue behind a minute of nothing.
    this.api.fetchCover(book.title, author)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: resp => resp.coverUrl ? addCoverAndFinish(resp.coverUrl) : this.inFlight.delete(book.md5),
        error: () => this.inFlight.delete(book.md5)
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
