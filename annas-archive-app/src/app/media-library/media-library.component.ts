import { Component, OnInit, OnDestroy } from '@angular/core';
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
type TileSize = 'small' | 'medium' | 'large';

const PLACEHOLDER_POSTER = '/assets/placeholder.jpg';
const QUEUE_POLL_MS = 10000;
/** The only three household members — mirrors the ebook library's fixed
 * "Dad's Books"/"Mom's Books"/"Paul's Books" owner set, minus the book-specific
 * wording since this filters both TV shows and movies. */
const OWNERS = ['Paul', 'Mom', 'Dad'];
const UNASSIGNED = 'Unassigned';

/** Sonarr/Radarr's images array isn't guaranteed poster-first — it can lead
 * with a banner or fanart/background image instead, which is why picking
 * images[0] blindly (the original bug here) crops oddly when forced into a
 * portrait frame. Same fix as MediaResultCardComponent.posterUrl. */
function posterUrlFor(result: MediaLookupResult): string {
  const poster = result.images?.find((i: { coverType: string }) => i.coverType === 'poster');
  return poster?.remoteUrl || poster?.url || PLACEHOLDER_POSTER;
}

function genresOf(result: MediaLookupResult): string[] {
  return result.customGenres ?? [];
}

function addedTimestamp(result: MediaLookupResult): number {
  return Date.parse((result['added'] as string) ?? '') || 0;
}

/** Radarr reports a movie's file size as a top-level `sizeOnDisk`; Sonarr
 * reports a series' *total* across all downloaded episode files as
 * `statistics.sizeOnDisk` — both ride along untouched via the raw-passthrough
 * index signature, same as `genres`/`hasFile` elsewhere in this file. */
function sizeOnDiskOf(result: MediaLookupResult): number {
  const seriesStats = result['statistics'] as { sizeOnDisk?: number } | undefined;
  return seriesStats?.sizeOnDisk ?? (result['sizeOnDisk'] as number | undefined) ?? 0;
}

function formatBytes(bytes: number): string | undefined {
  if (!bytes || bytes <= 0) return undefined;

  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let size = bytes;
  let unitIndex = 0;
  while (size >= 1024 && unitIndex < units.length - 1) {
    size /= 1024;
    unitIndex++;
  }
  return `${size.toFixed(1)} ${units[unitIndex]}`;
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
  styleUrl: './media-library.component.css'
})
export class MediaLibraryComponent implements OnInit, OnDestroy {
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
      this.api.getDownloadedMovies().subscribe({
        next: (movies) => {
          this.movieTiles = movies;
          this.loading = false;
        },
        error: (err) => this.handleLoadError(err)
      });
    } else {
      this.api.getDownloadedTv().subscribe({
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
    this.searchApi.getQueue().subscribe({
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
    const term = this.searchTerm.trim().toLowerCase();
    if (term && !(result.title || '').toLowerCase().includes(term)) return false;

    if (this.selectedGenre && !genresOf(result).includes(this.selectedGenre)) return false;

    if (this.selectedOwners.size > 0) {
      const itemOwners = result.owners ?? [];
      if (!itemOwners.some(o => this.selectedOwners.has(o))) return false;
    }

    // Favorites filter — cross-referenced against whichever owner filter buttons are
    // currently active, same convention as the ebook library; with no owner filter
    // active, anything favorited by any of the three household members counts.
    if (this.filterFavoritesOnly) {
      const favorites = result.favorites ?? [];
      if (favorites.length === 0) return false;
      if (this.selectedOwners.size > 0 && !favorites.some(o => this.selectedOwners.has(o))) return false;
    }

    return true;
  }

  private compare(a: MediaLookupResult, b: MediaLookupResult): number {
    switch (this.sortOrder) {
      case 'title':
        return (a.title || '').localeCompare(b.title || '');
      case 'year':
        return (b.year || 0) - (a.year || 0);
      case 'recent':
      default:
        return addedTimestamp(b) - addedTimestamp(a);
    }
  }

  toggleOwnerFilter(owner: string): void {
    if (this.selectedOwners.has(owner)) {
      this.selectedOwners.delete(owner);
    } else {
      this.selectedOwners.add(owner);
    }
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
    const merge = (existing: string[] | undefined, incoming: string[]): string[] => {
      if (incoming.length === 0) return existing ?? [];
      if (result.mode === 'replace') return incoming;
      return Array.from(new Set([...(existing ?? []), ...incoming]));
    };
    return {
      owners: merge(item.owners, result.owners),
      genres: merge(item.customGenres, result.genres)
    };
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
    return result.owners && result.owners.length > 0 ? result.owners.join(', ') : UNASSIGNED;
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
    this.api.watchMovie(tmdbId).subscribe({
      next: (resp) => {
        this.resolvingMovieId = null;
        this.dialog.open<JellyfinPlayerModalComponent, JellyfinPlayerModalData>(JellyfinPlayerModalComponent, {
          width: '90vw',
          maxWidth: '1100px',
          data: {
            title: movie.title,
            mode: resp.mode,
            embedUrl: resp.embedUrl,
            streamUrl: resp.mode === 'native' ? this.api.getMovieStreamUrl(tmdbId) : undefined,
            resumePositionSeconds: resp.resumePositionSeconds,
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

  /** Opens Jellyfin's proxied file download in a new tab — the browser handles
   * the actual save via the response's Content-Disposition: attachment header,
   * this just needs to not navigate the SPA away from itself. */
  downloadMovie(movie: MediaLookupResult, event: Event): void {
    event.stopPropagation(); // don't also trigger playMovie()
    if (movie.tmdbId === undefined) return;

    window.open(this.api.getMovieDownloadUrl(movie.tmdbId), '_blank');
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
