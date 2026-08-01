import { Injectable, OnDestroy } from '@angular/core';
import { Observable, Subject, takeUntil } from 'rxjs';

import { BookSearchApiService } from '../services/book-search-api.service';
import { LoggerService } from '../services/logger.service';
import { BookDto } from '../models/book-dto.model';
import { AUTO_DESCRIPTION_FETCH_LIMIT, DESCRIPTION_FETCH_STAGGER_MS } from '../constants';

interface DescriptionResponse {
  description?: string | null;
}

/** One rung of the fallback ladder: where to ask, and what to stamp on the book
 *  if that source answers. */
interface DescriptionSource {
  tag: string;
  fetch: (title: string, author?: string) => Observable<DescriptionResponse>;
}

/**
 * Fills in missing book descriptions, staggered and best-effort, by walking a
 * fallback ladder until one source answers.
 *
 * Split out of BookSearchComponent, where the ladder was four near-identical
 * private methods (`fetchDescriptionForBook` -> `tryOpenLibrary` ->
 * `tryWikipedia` -> `tryGPT4`), each hand-chaining the next one in both its
 * `next` and `error` handlers. Adding or reordering a source meant editing two
 * call sites in two different methods; here the ladder is one array, in order.
 *
 * Like BookCoverLookupService, provide it at component level so its timers and
 * subscriptions are torn down with the page.
 */
@Injectable()
export class BookDescriptionLookupService implements OnDestroy {
  private readonly timers = new Set<ReturnType<typeof setTimeout>>();
  private readonly destroy$ = new Subject<void>();

  /** Order is the fallback order. GPT-4 is last because it is the only one that
   *  costs money and the only one that can invent an answer. */
  private readonly sources: DescriptionSource[];

  constructor(
    private readonly api: BookSearchApiService,
    private readonly logger: LoggerService
  ) {
    this.sources = [
      { tag: 'googlebooks', fetch: (t, a) => this.api.fetchDescriptionFromGoogleBooks(t, a) },
      { tag: 'openlibrary', fetch: (t, a) => this.api.fetchDescriptionFromOpenLibrary(t, a) },
      { tag: 'wikipedia',   fetch: (t, a) => this.api.fetchDescriptionFromWikipedia(t, a) },
      { tag: 'gpt',         fetch: (t, a) => this.api.fetchDescriptionFromGPT4(t, a) }
    ];
  }

  ngOnDestroy(): void {
    this.timers.forEach(clearTimeout);
    this.timers.clear();
    this.destroy$.next();
    this.destroy$.complete();
  }

  /** Auto-fetch for the first N results, staggered to avoid a burst. */
  queueForBooks(books: BookDto[]): void {
    books.slice(0, AUTO_DESCRIPTION_FETCH_LIMIT).forEach((book, index) => {
      const handle = setTimeout(() => {
        this.timers.delete(handle);
        this.fetchFor(book);
      }, index * DESCRIPTION_FETCH_STAGGER_MS);
      this.timers.add(handle);
    });
  }

  /** Immediate fetch for one book — used when the user expands a card. */
  fetchOnDemand(book: BookDto): void {
    this.fetchFor(book);
  }

  private fetchFor(book: BookDto): void {
    if (book.description) return;  // already has one
    this.tryFrom(book, 0);
  }

  private tryFrom(book: BookDto, index: number): void {
    const source = this.sources[index];
    if (!source) return;  // ladder exhausted — the book stays without a description

    const next = () => this.tryFrom(book, index + 1);
    const isLast = index === this.sources.length - 1;

    source.fetch(book.title, book.authors?.[0])
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: resp => {
          if (resp.description) {
            book.description = resp.description;
            book.descriptionSource = source.tag;
          } else {
            next();
          }
        },
        error: err => {
          // Only the final rung is worth logging: every earlier failure is an
          // ordinary miss that the next source is expected to cover.
          if (isLast) {
            this.logger.error(`Failed to fetch description from ${source.tag}`, err);
          }
          next();
        }
      });
  }
}
