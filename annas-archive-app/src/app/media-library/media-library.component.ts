import { Component, DestroyRef, OnDestroy, OnInit, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Subscription, interval } from 'rxjs';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MediaLibraryApiService } from '../services/media-library-api.service';
import { MediaLookupResult, MediaQueueItem, MediaSearchApiService } from '../services/media-search-api.service';
import {
  JellyfinPlayerModalComponent,
  JellyfinPlayerModalData
} from '../components/jellyfin-player-modal/jellyfin-player-modal.component';
import { ConfirmDialogComponent, ConfirmDialogData } from '../components/confirm-dialog/confirm-dialog.component';
import { MediaEditDialogComponent, MediaEditDialogData, MediaEditDialogResult } from '../components/media-edit-dialog/media-edit-dialog.component';
import { MediaBulkEditDialogComponent, MediaBulkEditDialogData, MediaBulkEditDialogResult } from '../components/media-bulk-edit-dialog/media-bulk-edit-dialog.component';
import { MediaTileComponent } from '../components/shared/media-tile/media-tile.component';
import { ReleasePickerDialogComponent, ReleasePickerDialogData } from '../components/release-picker-dialog/release-picker-dialog.component';
import { LoggerService } from '../services/logger.service';
import { AuthService } from '../services/auth.service';
import { TileSizeControlsComponent } from '../components/shared/tile-size-controls/tile-size-controls.component';
import { matchesOwnerAndFavorites, toggleInSet } from '../shared/owner-filters';
import { HOUSEHOLD_OWNERS } from '../constants/owners';
import {
  PLACEHOLDER_POSTER, UNASSIGNED, addedTimestamp, compareMedia, genresOf, mergeBulkResult,
  ownerLabel, posterUrlFor
} from './media-library-view';
import { TileSize, formatBytes, matchesSearchTerm } from '../shared/media-grid';

interface LibraryTile {
  result: MediaLookupResult;
  downloadedSeasonCount: number;
  totalSeasonCount: number;
}

interface DownloadProgress {
  percent: number;
  /** e.g. "4.2 MB/s" — null until at least two polls have landed, since
   * speed is derived client-side from the change in sizeleft between polls
   * rather than a field Sonarr/Radarr's queue API provides directly. */
  speedLabel: string | null;
  /** Sonarr/Radarr's own formatted ETA string (e.g. "00:14:32"), taken as-is
   * from the queue record — they already compute this from their own
   * measured throughput, no reason to re-derive it. */
  etaLabel: string | null;
}

type SortOrder = 'title' | 'year' | 'recent';

const QUEUE_POLL_MS = 10000;
/** The household roster, from the one place that declares it. The ebook library
 * stores the same three people as "Paul's Books" tags; TV/movies store the bare
 * names, so this uses HOUSEHOLD_OWNERS directly rather than the book-tag form. */
const OWNERS = [...HOUSEHOLD_OWNERS];
/** Radarr reports a movie's file size as a top-level `sizeOnDisk`; Sonarr
 * reports a series' *total* across all downloaded episode files as
 * `statistics.sizeOnDisk` — both ride along untouched via the raw-passthrough
 * index signature, same as `genres`/`hasFile` elsewhere in this file. */
function sizeOnDiskOf(result: MediaLookupResult): number {
  const seriesStats = result['statistics'] as { sizeOnDisk?: number } | undefined;
  return seriesStats?.sizeOnDisk ?? (result['sizeOnDisk'] as number | undefined) ?? 0;
}

/**
 * Browse what's actually downloaded via Sonarr/Radarr — parallel to the
 * ebook Library page (search/genre/owner filters, tile-size + sort controls),
 * but backed by Sonarr/Radarr's own data instead of a local file scan (see
 * MediaLibraryEndpoints.cs for why: they already track file-existence
 * themselves). Owner(s) and custom genre tags are recorded server-side by
 * MediaMetadataService, keyed by Sonarr/Radarr's own record ID rather than
 * tagging the media files, since Sonarr/Radarr reorganize/rename those on
 * import — editable per show/movie via MediaEditDialogComponent.
 */
@Component({
  selector: 'app-media-library',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatButtonToggleModule,
    MatProgressSpinnerModule,
    MatButtonModule,
    MatIconModule,
    MatTooltipModule,
    MatCheckboxModule,
    MatDialogModule,
    MediaTileComponent,
    TileSizeControlsComponent,
  ],
  templateUrl: './media-library.component.html',
  styleUrl: './media-library.component.scss'
})
export class MediaLibraryComponent implements OnInit, OnDestroy {
  /**
   * Ends in-flight reads when the component is destroyed.
   *
   * Reads only: unsubscribing an HttpClient call aborts the request, so routing
   * a write through this would mean navigating away cancels the user's action.
   */
  private readonly destroyRef = inject(DestroyRef);

  /** false = TV, true = Movies — defaults to Movies (see ngOnInit). */
  showingMovies = true;
  loading = false;
  error: string | null = null;
  tvTiles: LibraryTile[] = [];
  movieTiles: MediaLookupResult[] = [];
  /** tmdbId of the movie currently resolving a watch URL, for a per-card spinner. */
  resolvingMovieId: number | null = null;

  searchTerm = '';
  selectedGenre = '';
  selectedOwners = new Set<string>();
  filterFavoritesOnly = false;
  sortOrder: SortOrder = 'recent';
  tileSize: TileSize = 'medium';

  /** Bulk genre/owner editing — keyed by Sonarr seriesId / Radarr movieId. */
  bulkEditMode = false;
  selectedForBulk = new Set<number>();

  /** Mobile: filters live in a bottom sheet toggled by the filter FAB. */
  sidebarCollapsed = false;

  /** Live download progress for anything still downloading, keyed by
   * Sonarr seriesId / Radarr movieId. */
  downloadProgress = new Map<number, DownloadProgress>();
  private prevQueueReadings = new Map<number, { sizeleft: number; timestamp: number }>();
  private queuePollSub?: Subscription;

  readonly owners = OWNERS;

  constructor(
    private api: MediaLibraryApiService,
    private searchApi: MediaSearchApiService,
    private dialog: MatDialog,
    private router: Router,
    private logger: LoggerService,
    private authService: AuthService
  ) {}

  ngOnInit(): void {
    // On mobile the filter sheet starts closed so the grid is what loads first
    if (window.innerWidth <= 768) {
      this.sidebarCollapsed = true;
    }

    // Default to showing the current session's own movies/shows — Mom sees
    // Mom's, Dad sees Dad's, and the admin (Paul) account sees Paul's, same
    // as the ebook library. Still just a normal toggle after this — anyone
    // can clear/change it from here.
    const ownerName = this.authService.getOwnerName();
    if (ownerName) {
      this.selectedOwners.add(ownerName);
    }

    this.load();
    this.refreshDownloadProgress();
    this.queuePollSub = interval(QUEUE_POLL_MS).subscribe(() => this.refreshDownloadProgress());
  }

  ngOnDestroy(): void {
    this.queuePollSub?.unsubscribe();
  }

  toggleShowing(): void {
    // Sonarr and Radarr each have their own independent ID sequence, so a TV
    // seriesId and a Radarr movieId can collide — clear any bulk selection
    // when switching tabs so it can't cross-apply to the wrong item type.
    this.selectedForBulk.clear();
    this.load();
  }

  private load(): void {
    this.loading = true;
    this.error = null;

    if (this.showingMovies) {
      this.api.getDownloadedMovies().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: (movies) => {
          this.movieTiles = movies;
          this.loading = false;
        },
        error: (err) => this.handleLoadError(err)
      });
    } else {
      this.api.getDownloadedTv().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: (series) => {
          this.tvTiles = series.map(result => ({
            result,
            downloadedSeasonCount: (result.seasons || []).filter(
              s => (s.statistics?.episodeFileCount ?? 0) > 0
            ).length,
            totalSeasonCount: (result.seasons || []).filter(s => s.seasonNumber !== 0).length
          }));
          this.loading = false;
        },
        error: (err) => this.handleLoadError(err)
      });
    }
  }

  private handleLoadError(err: unknown): void {
    this.logger.error('[MediaLibraryComponent] load failed', err);
    this.error = 'Could not load your library — is Sonarr/Radarr reachable?';
    this.loading = false;
  }

  getProgress(id: number | undefined): DownloadProgress | null {
    if (id === undefined) return null;
    return this.downloadProgress.get(id) ?? null;
  }

  /** Polls Sonarr/Radarr's shared queue endpoint and aggregates each item's
   * records by series/movie ID — a season can have several episodes
   * downloading at once, so a series' overall progress is the sum across
   * all of its in-flight queue records, not just one. Speed isn't a field
   * Sonarr/Radarr's queue API provides directly, so it's derived from the
   * change in sizeleft between polls (bytes recovered / seconds elapsed). */
  private refreshDownloadProgress(): void {
    this.searchApi.getQueue().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (queue) => {
        const now = Date.now();
        const nextProgress = new Map<number, DownloadProgress>();

        const grouped = new Map<number, MediaQueueItem[]>();
        for (const rec of queue.tv?.records ?? []) {
          if (rec.seriesId === undefined) continue;
          const group = grouped.get(rec.seriesId) ?? [];
          group.push(rec);
          grouped.set(rec.seriesId, group);
        }
        for (const rec of queue.movies?.records ?? []) {
          if (rec.movieId === undefined) continue;
          const group = grouped.get(rec.movieId) ?? [];
          group.push(rec);
          grouped.set(rec.movieId, group);
        }

        for (const [id, records] of grouped) {
          const totalSize = records.reduce((sum, r) => sum + (r.size ?? 0), 0);
          const totalLeft = records.reduce((sum, r) => sum + (r.sizeleft ?? 0), 0);
          if (totalSize <= 0) continue;

          const percent = Math.round(((totalSize - totalLeft) / totalSize) * 100);

          let speedLabel: string | null = null;
          const prev = this.prevQueueReadings.get(id);
          if (prev) {
            const bytesDelta = prev.sizeleft - totalLeft;
            const secondsDelta = (now - prev.timestamp) / 1000;
            if (bytesDelta > 0 && secondsDelta > 0) {
              const perSecond = formatBytes(bytesDelta / secondsDelta);
              speedLabel = perSecond ? `${perSecond}/s` : null;
            }
          }
          this.prevQueueReadings.set(id, { sizeleft: totalLeft, timestamp: now });

          nextProgress.set(id, {
            percent,
            speedLabel,
            etaLabel: records[0]?.timeleft ?? null
          });
        }

        this.downloadProgress = nextProgress;
      },
      error: (err) => this.logger.error('[MediaLibraryComponent] queue poll failed', err)
    });
  }

  get genres(): string[] {
    const items = this.showingMovies ? this.movieTiles : this.tvTiles.map(t => t.result);
    const set = new Set<string>();
    items.forEach(r => genresOf(r).forEach(g => set.add(g)));
    return Array.from(set).sort((a, b) => a.localeCompare(b));
  }

  get totalCount(): number {
    return this.showingMovies ? this.movieTiles.length : this.tvTiles.length;
  }

  get filteredTvTiles(): LibraryTile[] {
    return this.tvTiles
      .filter(t => this.matchesFilters(t.result))
      .sort((a, b) => this.compare(a.result, b.result));
  }

  get filteredMovieTiles(): MediaLookupResult[] {
    return this.movieTiles
      .filter(m => this.matchesFilters(m))
      .sort((a, b) => this.compare(a, b));
  }

  get filteredCount(): number {
    return this.showingMovies ? this.filteredMovieTiles.length : this.filteredTvTiles.length;
  }

  private matchesFilters(result: MediaLookupResult): boolean {
    if (!matchesSearchTerm(this.searchTerm, result.title)) return false;

    if (this.selectedGenre && !genresOf(result).includes(this.selectedGenre)) return false;

    return matchesOwnerAndFavorites(result, this.selectedOwners, this.filterFavoritesOnly);
  }

  private compare(a: MediaLookupResult, b: MediaLookupResult): number {
    return compareMedia(a, b, this.sortOrder);
  }

  toggleOwnerFilter(owner: string): void {
    toggleInSet(this.selectedOwners, owner);
  }

  toggleFavoritesFilter(): void {
    this.filterFavoritesOnly = !this.filterFavoritesOnly;
  }

  isFavorited(result: MediaLookupResult): boolean {
    const ownerName = this.authService.getOwnerName();
    return !!ownerName && (result.favorites ?? []).includes(ownerName);
  }

  toggleFavorite(result: MediaLookupResult, event: Event): void {
    event.stopPropagation(); // don't also trigger openSeries()/playMovie()
    if (result.id === undefined) return;
    const ownerName = this.authService.getOwnerName();
    if (!ownerName) return;

    const wasFavorited = this.isFavorited(result);
    const newValue = !wasFavorited;

    // Optimistic update
    result.favorites = newValue
      ? [...(result.favorites ?? []), ownerName]
      : (result.favorites ?? []).filter(o => o !== ownerName);

    const request$ = this.showingMovies
      ? this.api.setMovieFavorite(result.id, newValue)
      : this.api.setTvFavorite(result.id, newValue);

    request$.subscribe({
      next: (resp) => {
        result.favorites = resp.favorites;
      },
      error: (err) => {
        this.logger.error('[MediaLibraryComponent] toggleFavorite failed', err);
        // Revert on error
        result.favorites = wasFavorited
          ? [...(result.favorites ?? []), ownerName]
          : (result.favorites ?? []).filter(o => o !== ownerName);
      }
    });
  }

  setTileSize(size: TileSize): void {
    this.tileSize = size;
  }

  resetView(): void {
    this.searchTerm = '';
    this.selectedGenre = '';
    this.selectedOwners.clear();
    this.filterFavoritesOnly = false;
    this.sortOrder = 'recent';
    this.tileSize = 'medium';
    this.exitBulkEditMode();
  }

  toggleBulkEditMode(): void {
    this.bulkEditMode = !this.bulkEditMode;
    if (!this.bulkEditMode) {
      this.selectedForBulk.clear();
    }
  }

  /** Toggle the mobile filter sheet */
  toggleSidebar(): void {
    this.sidebarCollapsed = !this.sidebarCollapsed;
  }

  toggleTileSelection(id: number | undefined): void {
    if (!this.bulkEditMode || id === undefined) return;
    if (this.selectedForBulk.has(id)) {
      this.selectedForBulk.delete(id);
    } else {
      this.selectedForBulk.add(id);
    }
  }

  selectAllVisible(): void {
    if (!this.bulkEditMode) return;
    const ids = this.showingMovies
      ? this.filteredMovieTiles.map(m => m.id).filter((id): id is number => id !== undefined)
      : this.filteredTvTiles.map(t => t.result.id).filter((id): id is number => id !== undefined);
    ids.forEach(id => this.selectedForBulk.add(id));
  }

  private exitBulkEditMode(): void {
    this.bulkEditMode = false;
    this.selectedForBulk.clear();
  }

  openBulkEditDialog(): void {
    if (this.selectedForBulk.size === 0) return;

    const dialogRef = this.dialog.open<MediaBulkEditDialogComponent, MediaBulkEditDialogData, MediaBulkEditDialogResult>(
      MediaBulkEditDialogComponent,
      {
        width: '480px',
        data: {
          count: this.selectedForBulk.size,
          availableGenres: this.genres
        }
      }
    );

    dialogRef.afterClosed().subscribe(result => {
      if (!result) return;
      if (this.showingMovies) {
        this.applyBulkToMovies(result);
      } else {
        this.applyBulkToTv(result);
      }
    });
  }

  private mergeBulkResult(
    item: MediaLookupResult,
    result: MediaBulkEditDialogResult
  ): { owners: string[]; genres: string[] } {
    return mergeBulkResult(item, result);
  }

  private applyBulkToTv(result: MediaBulkEditDialogResult): void {
    const targets = this.tvTiles.filter(t => t.result.id !== undefined && this.selectedForBulk.has(t.result.id));
    const failedTitles: string[] = [];
    for (const tile of targets) {
      const { owners, genres } = this.mergeBulkResult(tile.result, result);
      this.api.setTvMetadata(tile.result.id!, owners, genres).subscribe({
        next: () => {
          tile.result.owners = owners;
          tile.result.customGenres = genres;
        },
        error: (err) => {
          this.logger.error('[MediaLibraryComponent] bulk setTvMetadata failed', err);
          failedTitles.push(tile.result.title);
          this.error = `Could not save changes for: ${failedTitles.join(', ')}`;
        }
      });
    }
    this.exitBulkEditMode();
  }

  private applyBulkToMovies(result: MediaBulkEditDialogResult): void {
    const targets = this.movieTiles.filter(m => m.id !== undefined && this.selectedForBulk.has(m.id));
    const failedTitles: string[] = [];
    for (const movie of targets) {
      const { owners, genres } = this.mergeBulkResult(movie, result);
      this.api.setMovieMetadata(movie.id!, owners, genres).subscribe({
        next: () => {
          movie.owners = owners;
          movie.customGenres = genres;
        },
        error: (err) => {
          this.logger.error('[MediaLibraryComponent] bulk setMovieMetadata failed', err);
          failedTitles.push(movie.title);
          this.error = `Could not save changes for: ${failedTitles.join(', ')}`;
        }
      });
    }
    this.exitBulkEditMode();
  }

  openSeries(tile: LibraryTile): void {
    this.router.navigate(['/media-library/series', tile.result.id]);
  }

  posterUrl(result: MediaLookupResult): string {
    return posterUrlFor(result);
  }

  ownerLabel(result: MediaLookupResult): string {
    return ownerLabel(result);
  }

  playMovie(movie: MediaLookupResult): void {
    if (movie.tmdbId === undefined || this.resolvingMovieId !== null) return;

    if (!movie['hasFile']) {
      this.error = `"${movie.title}" hasn't finished downloading yet.`;
      return;
    }

    this.error = null;
    this.resolvingMovieId = movie.tmdbId;
    const tmdbId = movie.tmdbId;
    this.api.watchMovie(tmdbId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (resp) => {
        this.resolvingMovieId = null;
        this.dialog.open<JellyfinPlayerModalComponent, JellyfinPlayerModalData>(JellyfinPlayerModalComponent, {
          width: '90vw',
          maxWidth: '1100px',
          data: {
            title: movie.title,
            mode: resp.mode,
            embedUrl: resp.embedUrl,
            streamUrl: resp.mode === 'native'
              ? (resp.playbackMode === 'transcode' ? this.api.getMovieHlsMasterUrl(tmdbId) : this.api.getMovieStreamUrl(tmdbId))
              : undefined,
            isHls: resp.playbackMode === 'transcode',
            resumePositionSeconds: resp.resumePositionSeconds,
            durationSeconds: resp.durationSeconds,
            audioTracks: resp.audioTracks,
            subtitleTracks: resp.subtitleTracks,
            subtitleUrlFor: resp.mode === 'native' && resp.mediaSourceId
              ? (subtitleIndex) => this.api.getMovieSubtitleUrl(tmdbId, resp.mediaSourceId!, subtitleIndex)
              : undefined,
            saveProgress: resp.mode === 'native'
              ? (positionSeconds) => this.api.saveMovieProgress(tmdbId, positionSeconds).subscribe({
                  error: (err) => this.logger.error('[MediaLibraryComponent] saveMovieProgress failed', err)
                })
              : undefined
          }
        });
      },
      error: (err) => {
        this.resolvingMovieId = null;
        this.logger.error('[MediaLibraryComponent] watchMovie failed', err);
        this.error = `Jellyfin hasn't matched "${movie.title}" yet — it may still be scanning.`;
      }
    });
  }

  /** Triggers Jellyfin's proxied file download via the response's
   * Content-Disposition: attachment header — navigating the CURRENT tab to
   * an attachment response makes the browser divert to its download handler
   * instead of actually replacing the page, so the SPA is never navigated
   * away from. Deliberately not window.open(url, '_blank'): that opens a
   * real new tab/window, which popup blockers (DuckDuckGo's browser in
   * particular) treat as a suspicious popup needing explicit permission —
   * and since the response has no HTML to render, the "allowed" tab is just
   * blank anyway. No new window is requested here, so there's nothing for a
   * popup blocker to catch. */
  downloadMovie(movie: MediaLookupResult, event: Event): void {
    event.stopPropagation(); // don't also trigger playMovie()
    if (movie.tmdbId === undefined) return;

    window.location.href = this.api.getMovieDownloadUrl(movie.tmdbId);
  }

  openTvEditDialog(tile: LibraryTile, event: Event): void {
    event.stopPropagation(); // don't also trigger openSeries()
    if (tile.result.id === undefined) return;

    const dialogData: MediaEditDialogData = {
      title: tile.result.title,
      genres: tile.result.customGenres ?? [],
      owners: tile.result.owners ?? [],
      availableGenres: this.genres,
      sizeLabel: formatBytes(sizeOnDiskOf(tile.result)),
      favoritedBy: tile.result.favorites ?? [],
      mediaType: 'tv',
      id: tile.result.id
    };

    const dialogRef = this.dialog.open<MediaEditDialogComponent, MediaEditDialogData, MediaEditDialogResult>(
      MediaEditDialogComponent,
      { width: '480px', data: dialogData }
    );

    dialogRef.afterClosed().subscribe(result => {
      // The favorite toggle inside the dialog applies immediately (its own API call) —
      // sync whatever it ended up at back onto the tile here.
      tile.result.favorites = dialogData.favoritedBy;

      if (!result) return;
      this.api.setTvMetadata(tile.result.id!, result.owners, result.genres).subscribe({
        next: () => {
          tile.result.owners = result.owners;
          tile.result.customGenres = result.genres;
        },
        error: (err) => {
          this.logger.error('[MediaLibraryComponent] setTvMetadata failed', err);
          this.error = `Could not save changes for "${tile.result.title}" — please try again.`;
        }
      });
    });
  }

  openMovieEditDialog(movie: MediaLookupResult, event: Event): void {
    event.stopPropagation(); // don't also trigger playMovie()
    if (movie.id === undefined) return;

    const dialogData: MediaEditDialogData = {
      title: movie.title,
      genres: movie.customGenres ?? [],
      owners: movie.owners ?? [],
      availableGenres: this.genres,
      sizeLabel: formatBytes(sizeOnDiskOf(movie)),
      favoritedBy: movie.favorites ?? [],
      mediaType: 'movie',
      id: movie.id
    };

    const dialogRef = this.dialog.open<MediaEditDialogComponent, MediaEditDialogData, MediaEditDialogResult>(
      MediaEditDialogComponent,
      { width: '480px', data: dialogData }
    );

    dialogRef.afterClosed().subscribe(result => {
      // The favorite toggle inside the dialog applies immediately (its own API call) —
      // sync whatever it ended up at back onto the movie here.
      movie.favorites = dialogData.favoritedBy;

      if (!result) return;
      this.api.setMovieMetadata(movie.id!, result.owners, result.genres).subscribe({
        next: () => {
          movie.owners = result.owners;
          movie.customGenres = result.genres;
        },
        error: (err) => {
          this.logger.error('[MediaLibraryComponent] setMovieMetadata failed', err);
          this.error = `Could not save changes for "${movie.title}" — please try again.`;
        }
      });
    });
  }

  /** Radarr auto-rejects releases that don't fit the quality profile (e.g.
   * too large under a size-capped profile) and just leaves the movie
   * "missing" with no visibility into why — this opens every release its
   * indexers found, rejected ones included, so the user can grab an
   * oversized one themselves when nothing smaller is available. */
  openMovieReleasePicker(movie: MediaLookupResult, event: Event): void {
    event.stopPropagation(); // don't also trigger playMovie()
    if (movie.id === undefined) return;
    const movieId = movie.id;

    const dialogRef = this.dialog.open<ReleasePickerDialogComponent, ReleasePickerDialogData, boolean>(
      ReleasePickerDialogComponent,
      {
        width: '600px',
        data: {
          title: `Find releases — ${movie.title}`,
          fetch: () => this.api.searchMovieReleases(movieId),
          grab: (release) => this.api.grabMovieRelease(movieId, release)
        }
      }
    );

    dialogRef.afterClosed().subscribe(grabbed => {
      if (grabbed) this.refreshDownloadProgress();
    });
  }

  deleteSeries(tile: LibraryTile, event: Event): void {
    event.stopPropagation(); // don't also trigger openSeries()

    const dialogRef = this.dialog.open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
      width: '450px',
      data: {
        title: 'Delete Show',
        message: `Delete "${tile.result.title}" and all its downloaded files?\n\nThis cannot be undone.`,
        confirmText: 'Delete',
        isDanger: true
      }
    });

    dialogRef.afterClosed().subscribe(confirmed => {
      if (!confirmed || tile.result.id === undefined) return;
      this.api.deleteSeries(tile.result.id).subscribe({
        next: () => {
          this.tvTiles = this.tvTiles.filter(t => t !== tile);
        },
        error: (err) => {
          this.logger.error('[MediaLibraryComponent] deleteSeries failed', err);
          this.error = `Could not delete "${tile.result.title}".`;
        }
      });
    });
  }

  deleteMovie(movie: MediaLookupResult, event: Event): void {
    event.stopPropagation(); // don't also trigger playMovie()

    const dialogRef = this.dialog.open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
      width: '450px',
      data: {
        title: 'Delete Movie',
        message: `Delete "${movie.title}" and its downloaded file?\n\nThis cannot be undone.`,
        confirmText: 'Delete',
        isDanger: true
      }
    });

    dialogRef.afterClosed().subscribe(confirmed => {
      if (!confirmed || movie.id === undefined) return;
      this.api.deleteMovie(movie.id).subscribe({
        next: () => {
          this.movieTiles = this.movieTiles.filter(m => m !== movie);
        },
        error: (err) => {
          this.logger.error('[MediaLibraryComponent] deleteMovie failed', err);
          this.error = `Could not delete "${movie.title}".`;
        }
      });
    });
  }
}
