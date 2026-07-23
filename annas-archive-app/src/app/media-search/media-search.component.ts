import { Component, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { forkJoin, firstValueFrom, Subscription, interval } from 'rxjs';
import {
  MediaSearchApiService,
  MediaLookupResult,
  MediaQueueItem
} from '../services/media-search-api.service';
import {
  MediaResultCardComponent,
  MediaAddState
} from '../components/media-result-card/media-result-card.component';
import { SeasonPickerModalComponent, SeasonPickerModalData } from '../season-picker-modal/season-picker-modal.component';
import { MediaLibraryApiService } from '../services/media-library-api.service';
import { AiApiService, AiMediaSearchItem } from '../services/ai-api.service';
import { LoggerService } from '../services/logger.service';

type MediaType = 'tv' | 'movie';

interface ResultEntry {
  result: MediaLookupResult;
  mediaType: MediaType;
  addState: MediaAddState;
  progressLabel: string | null;
  addedId?: number;
  /** Set when this TV result matches a series already in Sonarr (by tvdbId) —
   * lets the season picker default to current state and routes "Add" through
   * updateTvSeasons instead of re-adding the series from scratch. */
  existingSeriesId?: number;
  /** Seasons requested (monitored), whether or not the files have actually
   * arrived yet — distinct from downloadedSeasons below. */
  alreadyAddedSeasons?: number[];
  /** Seasons that actually have downloaded episode files, per Sonarr's own
   * per-season episodeFileCount — the real "ready to watch" signal. */
  downloadedSeasons?: number[];
}

const QUEUE_POLL_MS = 10000;
/** How many Sonarr/Radarr lookups run at once while resolving AI-suggested
 * titles — same reasoning and same shape as related-books-modal's
 * MATCH_SEARCH_CONCURRENCY: unbounded concurrency here backfired badly for
 * Anna's Archive earlier this session, so every "many small lookups at
 * once" spot in this app caps it deliberately. */
const AI_RESOLVE_CONCURRENCY = 3;

async function runWithConcurrencyLimit<T, R>(
  items: T[],
  limit: number,
  fn: (item: T, index: number) => Promise<R>
): Promise<R[]> {
  const results: R[] = new Array(items.length);
  let nextIndex = 0;

  async function worker(): Promise<void> {
    while (nextIndex < items.length) {
      const index = nextIndex++;
      results[index] = await fn(items[index], index);
    }
  }

  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, () => worker()));
  return results;
}

/**
 * Search-and-acquire page for TV/movies — same one-flow UX as the book
 * search page, fronting Sonarr (TV) / Radarr (movies) via MediaSearchApiService
 * instead of linking out to their separate dashboards. Also supports an AI
 * natural-language search mode (same pattern as the book search page's
 * robot-icon toggle), which can return a mix of TV and movie results in one
 * go — that's why each result tracks its own mediaType instead of the whole
 * page relying on a single TV/Movie toggle.
 */
@Component({
  selector: 'app-media-search',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatIconModule,
    MatButtonToggleModule,
    MatProgressSpinnerModule,
    MatDialogModule,
    MediaResultCardComponent
  ],
  templateUrl: './media-search.component.html',
  styleUrl: './media-search.component.css'
})
export class MediaSearchComponent implements OnDestroy {
  searchTerm = '';
  /** false = TV (Sonarr), true = Movies (Radarr) — only meaningful for
   * normal (non-AI) search, since AI results carry their own per-item type. */
  searchingMovies = false;
  loading = false;
  error: string | null = null;
  entries: ResultEntry[] = [];

  aiSearchExpanded = false;
  aiSearchQuery = '';

  private queueSub: Subscription;

  constructor(
    private api: MediaSearchApiService,
    private libraryApi: MediaLibraryApiService,
    private aiApi: AiApiService,
    private dialog: MatDialog,
    private logger: LoggerService
  ) {
    this.queueSub = interval(QUEUE_POLL_MS).subscribe(() => this.refreshQueueStatuses());
  }

  ngOnDestroy(): void {
    this.queueSub.unsubscribe();
  }

  toggleAiSearch(): void {
    this.aiSearchExpanded = !this.aiSearchExpanded;
    this.aiSearchQuery = this.aiSearchExpanded ? this.searchTerm.trim() : '';
  }

  /** Plain Enter submits the AI query; Shift+Enter inserts a newline as normal. */
  onAiTextareaEnter(event: Event): void {
    if ((event as KeyboardEvent).shiftKey) return;
    event.preventDefault();
    this.onSearch();
  }

  onSearch(): void {
    if (this.aiSearchExpanded) {
      this.runAiSearch();
      return;
    }

    const term = this.searchTerm.trim();
    if (!term) return;

    this.loading = true;
    this.error = null;
    this.entries = [];

    const mediaType: MediaType = this.searchingMovies ? 'movie' : 'tv';
    const search$ = this.searchingMovies ? this.api.searchMovies(term) : this.api.searchTv(term);
    search$.subscribe({
      next: (results) => {
        this.entries = results.map(result => ({
          result,
          mediaType,
          addState: 'idle' as MediaAddState,
          progressLabel: null
        }));
        this.loading = false;
        this.crossReferenceLibrary();
      },
      error: (err) => {
        this.logger.error('[MediaSearchComponent] search failed', err);
        this.error = 'Search failed — is Sonarr/Radarr reachable?';
        this.loading = false;
      }
    });
  }

  private runAiSearch(): void {
    const query = this.aiSearchQuery.trim();
    if (!query) return;

    this.loading = true;
    this.error = null;
    this.entries = [];

    this.aiApi.aiMediaSearch(query).subscribe({
      next: (aiResult) => this.resolveAiResults(aiResult.results || []),
      error: (err) => {
        this.logger.error('[MediaSearchComponent] AI search failed', err);
        this.error = err?.error?.error || 'AI search failed — try rephrasing the query.';
        this.loading = false;
      }
    });
  }

  /** Resolves each AI-suggested title into a real Sonarr/Radarr result by
   * calling the exact same lookup the normal search flow uses — takes the
   * first match per title (Sonarr/Radarr's own lookup already ranks by
   * relevance) and skips anything that resolves to nothing. */
  private resolveAiResults(items: AiMediaSearchItem[]): void {
    runWithConcurrencyLimit(items, AI_RESOLVE_CONCURRENCY, async (item) => {
      const term = item.year ? `${item.title} (${item.year})` : item.title;
      const results$ = item.type === 'movie' ? this.api.searchMovies(term) : this.api.searchTv(term);
      try {
        const results = await firstValueFrom(results$);
        return results?.[0] ? { result: results[0], mediaType: item.type as MediaType } : null;
      } catch (err) {
        this.logger.error(`[MediaSearchComponent] AI resolve failed for "${item.title}"`, err);
        return null;
      }
    }).then(resolved => {
      this.entries = resolved
        .filter((r): r is { result: MediaLookupResult; mediaType: MediaType } => r !== null)
        .map(({ result, mediaType }) => ({
          result,
          mediaType,
          addState: 'idle' as MediaAddState,
          progressLabel: null
        }));
      this.loading = false;
      if (this.entries.length === 0) {
        this.error = 'None of the AI-suggested titles could be found — try rephrasing the query.';
      }
      this.crossReferenceLibrary();
    });
  }

  /** Cross-references the just-fetched results (single-type from a normal
   * search, or mixed from an AI search) against what's already added in
   * Sonarr/Radarr, so already-requested/downloaded content doesn't look
   * like a blank slate. */
  private crossReferenceLibrary(): void {
    const needsTv = this.entries.some(e => e.mediaType === 'tv');
    const needsMovies = this.entries.some(e => e.mediaType === 'movie');
    if (!needsTv && !needsMovies) return;

    forkJoin({
      tv: needsTv ? this.api.getTvLibrary() : Promise.resolve<MediaLookupResult[]>([]),
      movies: needsMovies ? this.libraryApi.getDownloadedMovies() : Promise.resolve<MediaLookupResult[]>([])
    }).subscribe({
      next: ({ tv, movies }) => {
        const byTvdbId = new Map(tv.filter(s => s.tvdbId !== undefined).map(s => [s.tvdbId, s]));
        const byTmdbId = new Map(movies.filter(m => m.tmdbId !== undefined).map(m => [m.tmdbId, m]));

        for (const entry of this.entries) {
          if (entry.mediaType === 'tv') {
            const existing = entry.result.tvdbId !== undefined ? byTvdbId.get(entry.result.tvdbId) : undefined;
            if (!existing) continue;
            entry.existingSeriesId = existing.id;
            entry.alreadyAddedSeasons = (existing.seasons || []).filter(s => s.monitored).map(s => s.seasonNumber);
            entry.downloadedSeasons = (existing.seasons || [])
              .filter(s => (s.statistics?.episodeFileCount ?? 0) > 0)
              .map(s => s.seasonNumber);
          } else {
            const existing = entry.result.tmdbId !== undefined ? byTmdbId.get(entry.result.tmdbId) : undefined;
            if (!existing) continue;
            entry.addState = 'added';
            entry.addedId = existing.id;
            entry.progressLabel = existing['hasFile'] ? 'Already have ✔' : 'Requested — downloading…';
          }
        }
      },
      error: (err) => this.logger.error('[MediaSearchComponent] library cross-reference failed', err)
    });
  }

  onAdd(entry: ResultEntry): void {
    // Movies have no seasons — add directly. Checking the result itself
    // (rather than entry.mediaType) is deliberate: it's the same signal
    // either way, and stays correct even for a mixed AI result set.
    if (!entry.result.seasons?.length) {
      this.doAdd(entry);
      return;
    }

    const dialogRef = this.dialog.open<SeasonPickerModalComponent, SeasonPickerModalData, number[] | undefined>(
      SeasonPickerModalComponent,
      {
        width: '480px',
        data: {
          title: entry.result.title,
          seasons: entry.result.seasons,
          alreadyAddedSeasons: entry.alreadyAddedSeasons
        }
      }
    );

    dialogRef.afterClosed().subscribe(selectedSeasons => {
      if (selectedSeasons === undefined) return; // cancelled
      this.doAdd(entry, selectedSeasons);
    });
  }

  private doAdd(entry: ResultEntry, selectedSeasons?: number[]): void {
    entry.addState = 'adding';
    const add$ = entry.mediaType === 'movie'
      ? this.api.addMovie(entry.result)
      : entry.existingSeriesId !== undefined
        ? this.api.updateTvSeasons(entry.existingSeriesId, selectedSeasons || [])
        : this.api.addTvShow(entry.result, selectedSeasons);
    add$.subscribe({
      next: (added) => {
        entry.addState = 'added';
        entry.progressLabel = 'Queued';
        entry.addedId = added.id;
      },
      error: (err) => {
        this.logger.error('[MediaSearchComponent] add failed', err);
        entry.addState = 'error';
      }
    });
  }

  private refreshQueueStatuses(): void {
    const pending = this.entries.filter(e => e.addState === 'added' && e.addedId !== undefined);
    if (pending.length === 0) return;

    this.api.getQueue().subscribe({
      next: (queue) => {
        const tvRecords = queue.tv.records || [];
        const movieRecords = queue.movies.records || [];
        for (const entry of pending) {
          const records = entry.mediaType === 'movie' ? movieRecords : tvRecords;
          const match = records.find(r =>
            entry.mediaType === 'movie' ? r.movieId === entry.addedId : r.seriesId === entry.addedId
          );
          entry.progressLabel = match ? this.describeQueueItem(match) : 'Imported ✔';
        }
      },
      error: (err) => this.logger.error('[MediaSearchComponent] queue poll failed', err)
    });
  }

  private describeQueueItem(item: MediaQueueItem): string {
    if (item.size && item.sizeleft !== undefined && item.size > 0) {
      const pct = Math.round(((item.size - item.sizeleft) / item.size) * 100);
      return `${pct}%${item.timeleft ? ' · ' + item.timeleft : ''}`;
    }
    return item.status || 'Queued';
  }
}
