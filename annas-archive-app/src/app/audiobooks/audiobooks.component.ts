import { Component, OnDestroy, OnInit } from '@angular/core';
import { Subject, takeUntil } from 'rxjs';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { AudiobookApiService, AudiobookItem } from '../services/audiobook-api.service';
import { AudiobookRequestApiService, AudiobookRequestStatus } from '../services/audiobook-request-api.service';
import { MediaEditDialogComponent, MediaEditDialogData, MediaEditDialogResult } from '../components/media-edit-dialog/media-edit-dialog.component';
import { MediaBulkEditDialogComponent, MediaBulkEditDialogData, MediaBulkEditDialogResult } from '../components/media-bulk-edit-dialog/media-bulk-edit-dialog.component';
import { MediaTileComponent } from '../components/shared/media-tile/media-tile.component';
import {
  AudiobookPlayerDialogComponent,
  AudiobookPlayerDialogData
} from '../components/audiobook-player-dialog/audiobook-player-dialog.component';
import { LoggerService } from '../services/logger.service';
import { AuthService } from '../services/auth.service';
import { TileSizeControlsComponent } from '../components/shared/tile-size-controls/tile-size-controls.component';
import { ActivatedRoute } from '@angular/router';
import { matchesOwnerAndFavorites, toggleInSet } from '../shared/owner-filters';
import { HOUSEHOLD_OWNERS } from '../constants/owners';
import { TileSize, formatBytes, matchesSearchTerm } from '../shared/media-grid';

type SortOrder = 'title' | 'author' | 'recent';

const PLACEHOLDER_COVER = '/assets/placeholder.jpg';
/** The household roster, from the one place that declares it. */
const OWNERS = [...HOUSEHOLD_OWNERS];

/** Request states still moving. Anything else has settled and needs no polling. */
const ACTIVE_REQUEST_STATES = new Set([
  'Searching', 'Queued', 'Downloading', 'Paused', 'Processing', 'Importing', 'ReadyToScan'
]);
/** Matches the search page's cadence, and the server fetches Listenarr's queue once per call. */
const REQUEST_POLL_MS = 10_000;

/** Human wording per request state. Deliberately plain — the point of these
 *  cards is that it's obvious the book is not ready to play yet. */
const REQUEST_STATE_LABELS: Record<string, string> = {
  // "Monitored" means Listenarr is tracking the book but has not grabbed
  // anything — with Listenarr:AutoSearch off, it sits here until a release is
  // picked by hand. The raw state name reads as jargon on a library shelf, and
  // it is the most common state these cards will show.
  Monitored: 'Not downloading yet — pick a release',
  Searching: 'Looking for a release',
  Queued: 'Waiting to download',
  Downloading: 'Downloading',
  Paused: 'Paused',
  Processing: 'Processing',
  Importing: 'Importing',
  ReadyToScan: 'Almost ready',
  ImportBlocked: 'Needs attention',
  Failed: 'Failed',
  Canceled: 'Canceled'
};

/** States where a progress bar is meaningful. "Monitored" has no download to measure. */
const PROGRESS_STATES = new Set(['Downloading', 'Paused', 'Processing', 'Importing']);

function requestStateLabel(state: string): string {
  return REQUEST_STATE_LABELS[state] ?? state;
}

function genresOf(item: AudiobookItem): string[] {
  return item.customGenres ?? [];
}

// Audiobookshelf nests almost everything under `media`/`media.metadata` rather
// than flat top-level fields (confirmed via live API inspection) — same
// raw-passthrough-plus-helper-functions idiom as posterUrlFor()/genresOf() in
// media-library.component.ts for Sonarr/Radarr's raw shape.
function titleOf(item: AudiobookItem): string {
  return item.media?.metadata?.title ?? 'Untitled';
}

function authorOf(item: AudiobookItem): string | undefined {
  return item.media?.metadata?.authorName || undefined;
}

function narratorOf(item: AudiobookItem): string | undefined {
  return item.media?.metadata?.narratorName || undefined;
}

function seriesOf(item: AudiobookItem): string | undefined {
  return item.media?.metadata?.seriesName || undefined;
}

function durationOf(item: AudiobookItem): number | undefined {
  return item.media?.duration;
}

function hasCover(item: AudiobookItem): boolean {
  return !!item.media?.coverPath || !!item.hasCustomCover;
}

function addedTimestamp(item: AudiobookItem): number {
  // Audiobookshelf's own "addedAt" field, epoch ms — rides along untouched
  // via the raw-passthrough index signature, same idiom as the TV/movie
  // library's addedTimestamp() reading Sonarr/Radarr's "added" field.
  const raw = item['addedAt'];
  return typeof raw === 'number' ? raw : 0;
}

function formatDuration(seconds: number | undefined): string | undefined {
  if (!seconds || seconds <= 0) return undefined;
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.round((seconds % 3600) / 60);
  if (hours === 0) return `${minutes}m`;
  return `${hours}h ${minutes}m`;
}

/**
 * Browse the audiobook catalog — parallel to the Media Library page (search/
 * genre/owner/favorites filters, tile-size + sort controls), but a single
 * content type (no TV/Movie toggle) backed by Audiobookshelf instead of
 * Sonarr/Radarr (see AudiobookLibraryEndpoints.cs for why: there's no
 * Readarr-equivalent to integrate with the request/acquire flow, so this is
 * purely "browse what Audiobookshelf has already cataloged"). Owner(s) and
 * genre tags are recorded server-side by MediaMetadataService, same as the
 * TV/movie library, keyed by Audiobookshelf's own item id.
 */
@Component({
  selector: 'app-audiobooks',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatProgressSpinnerModule,
    MatButtonModule,
    MatIconModule,
    MatTooltipModule,
    MatDialogModule,
    MediaTileComponent,
    TileSizeControlsComponent,
  ],
  templateUrl: './audiobooks.component.html',
  styleUrl: './audiobooks.component.scss'
})
export class AudiobooksComponent implements OnInit, OnDestroy {
  loading = false;
  error: string | null = null;
  items: AudiobookItem[] = [];

  /**
   * Books I've requested that Audiobookshelf can't know about yet — they have
   * no file on disk, so they never appear in getCatalog(). Rendered as dimmed
   * "ghost" tiles alongside the real library so an in-flight download is
   * visible somewhere other than the search page it was started from.
   */
  pendingRequests: AudiobookRequestStatus[] = [];
  private requestPollTimer: number | null = null;

  searchTerm = '';
  selectedGenre = '';
  selectedOwners = new Set<string>();
  filterFavoritesOnly = false;
  sortOrder: SortOrder = 'recent';
  tileSize: TileSize = 'medium';

  /** Bulk genre/owner editing — keyed by Audiobookshelf's string item id. */
  bulkEditMode = false;
  selectedForBulk = new Set<string>();

  /** Mobile: filters live in a bottom sheet toggled by the filter FAB. */
  sidebarCollapsed = false;

  /** Bumped after a cover override save so coverUrl()'s cache-busting query
   * param changes — cover responses carry a 1-day Cache-Control, so without
   * this the browser would keep showing the pre-edit cached image. */
  private coverVersions = new Map<string, number>();
  private deepLinkItemId: string | null = null;

  readonly owners = OWNERS;

  constructor(
    private api: AudiobookApiService,
    private requestApi: AudiobookRequestApiService,
    private dialog: MatDialog,
    private logger: LoggerService,
    private authService: AuthService,
    private route: ActivatedRoute
  ) {}

  ngOnInit(): void {
    this.deepLinkItemId = this.route.snapshot.queryParamMap.get('item');
    // On mobile the filter sheet starts closed so the grid is what loads first
    if (window.innerWidth <= 768) {
      this.sidebarCollapsed = true;
    }

    // Default to showing the current session's own audiobooks — same
    // convention as the ebook/media libraries.
    const ownerName = this.authService.getOwnerName();
    if (ownerName) {
      this.selectedOwners.add(ownerName);
    }

    this.load();
    this.loadPendingRequests();
  }

  /**
   * Ends every in-flight request when the component goes.
   *
   * Not just tidiness: `loadPendingRequests` restarts the poll timer from its
   * own response handler, so a fetch that resolved after destroy used to start
   * a fresh interval on a dead component — one nothing would ever clear, and
   * another one for every visit back to the page.
   */
  private readonly destroy$ = new Subject<void>();

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
    this.stopRequestPolling();
  }

  // ─── Pending requests (ghost tiles) ────────────────────────────────────

  /**
   * A second, independent fetch: the catalog comes from Audiobookshelf, this
   * comes from Listenarr. Failure here is deliberately silent — a Listenarr
   * outage must not stop the playable library from rendering.
   */
  private loadPendingRequests(): void {
    this.requestApi.listMyRequests().pipe(takeUntil(this.destroy$)).subscribe({
      next: (requests) => {
        this.pendingRequests = requests;
        this.syncRequestPolling();
      },
      error: (err) => {
        this.logger.error('[AudiobooksComponent] pending requests failed', err);
        this.pendingRequests = [];
        this.stopRequestPolling();
      }
    });
  }

  /** Polls only while something is actually moving, then stops on its own. */
  private syncRequestPolling(): void {
    const hasActive = this.pendingRequests.some(r => ACTIVE_REQUEST_STATES.has(r.state));
    if (!hasActive) {
      this.stopRequestPolling();
      return;
    }

    if (this.requestPollTimer !== null) return;
    this.requestPollTimer = window.setInterval(() => this.loadPendingRequests(), REQUEST_POLL_MS);
  }

  private stopRequestPolling(): void {
    if (this.requestPollTimer !== null) window.clearInterval(this.requestPollTimer);
    this.requestPollTimer = null;
  }

  get visiblePendingRequests(): AudiobookRequestStatus[] {
    // Respects the search box so a filtered grid doesn't keep showing unrelated
    // ghosts, but ignores genre/owner/favorite filters — a book that hasn't
    // downloaded has none of that metadata yet and would always be filtered out.
    const term = this.searchTerm.trim().toLowerCase();
    if (!term) return this.pendingRequests;
    return this.pendingRequests.filter(r => r.title.toLowerCase().includes(term));
  }

  requestStateLabel(request: AudiobookRequestStatus): string {
    return requestStateLabel(request.state);
  }

  requestProgressPercent(request: AudiobookRequestStatus): number {
    return Math.max(0, Math.min(100, Math.round(request.progress ?? 0)));
  }

  requestSubtitle(request: AudiobookRequestStatus): string {
    const parts: string[] = [this.requestStateLabel(request)];
    if (request.state === 'Downloading') {
      const size = formatBytes(request.totalSize);
      parts.push(size ? `${this.requestProgressPercent(request)}% of ${size}` : `${this.requestProgressPercent(request)}%`);
    }
    return parts.join(' · ');
  }

  showsProgress(request: AudiobookRequestStatus): boolean {
    return PROGRESS_STATES.has(request.state);
  }

  /** Exposed for the template — pending tiles have no cover of their own. */
  readonly placeholderCover = PLACEHOLDER_COVER;

  /** Audiobook art is square (Audible's is), unlike the 2:3 the shared tile
   *  defaults to for movie posters and ebook jackets. Applied to the pending
   *  ghost tiles too, so a filled row and a waiting row line up. */
  readonly audiobookCoverAspect = '1 / 1';

  isRequestFailed(request: AudiobookRequestStatus): boolean {
    return request.state === 'Failed' || request.state === 'ImportBlocked';
  }

  requestTooltip(request: AudiobookRequestStatus): string {
    if (request.error) return request.error;
    if (request.importBlockMessages?.length) return request.importBlockMessages.join(' ');
    return `${request.title} — ${this.requestStateLabel(request)}. Not downloaded yet.`;
  }

  /** Removes it from my view only; the request itself and the other requesters are untouched. */
  dismissRequest(request: AudiobookRequestStatus, event: Event): void {
    event.stopPropagation();
    const previous = this.pendingRequests;
    this.pendingRequests = previous.filter(r => r.listenarrId !== request.listenarrId);

    this.requestApi.dismissRequest(request.listenarrId).subscribe({
      error: (err) => {
        this.logger.error('[AudiobooksComponent] dismiss failed', err);
        this.pendingRequests = previous; // put it back rather than lie about it
      }
    });
  }

  retryRequestImport(request: AudiobookRequestStatus, event: Event): void {
    event.stopPropagation();
    this.requestApi.retryImport(request.listenarrId).subscribe({
      next: () => this.loadPendingRequests(),
      error: (err) => this.logger.error('[AudiobooksComponent] retry import failed', err)
    });
  }

  private load(): void {
    this.loading = true;
    this.error = null;

    this.api.getCatalog().pipe(takeUntil(this.destroy$)).subscribe({
      next: (items) => {
        this.items = items;
        this.loading = false;
        if (this.deepLinkItemId) {
          const item = items.find(candidate => candidate.id === this.deepLinkItemId);
          this.deepLinkItemId = null;
          if (item) this.openPlayer(item);
        }
      },
      error: (err) => {
        this.logger.error('[AudiobooksComponent] load failed', err);
        this.error = 'Could not load your audiobook library — is Audiobookshelf reachable?';
        this.loading = false;
      }
    });
  }

  get genres(): string[] {
    const set = new Set<string>();
    this.items.forEach(item => genresOf(item).forEach(g => set.add(g)));
    return Array.from(set).sort((a, b) => a.localeCompare(b));
  }

  get totalCount(): number {
    return this.items.length;
  }

  get filteredItems(): AudiobookItem[] {
    return this.items
      .filter(item => this.matchesFilters(item))
      .sort((a, b) => this.compare(a, b));
  }

  get filteredCount(): number {
    return this.filteredItems.length;
  }

  private matchesFilters(item: AudiobookItem): boolean {
    if (!matchesSearchTerm(this.searchTerm, titleOf(item), authorOf(item), narratorOf(item), seriesOf(item))) return false;

    if (this.selectedGenre && !genresOf(item).includes(this.selectedGenre)) return false;

    return matchesOwnerAndFavorites(item, this.selectedOwners, this.filterFavoritesOnly);
  }

  private compare(a: AudiobookItem, b: AudiobookItem): number {
    switch (this.sortOrder) {
      case 'title':
        return titleOf(a).localeCompare(titleOf(b));
      case 'author':
        return (authorOf(a) || '').localeCompare(authorOf(b) || '');
      case 'recent':
      default:
        return addedTimestamp(b) - addedTimestamp(a);
    }
  }

  toggleOwnerFilter(owner: string): void {
    toggleInSet(this.selectedOwners, owner);
  }

  toggleFavoritesFilter(): void {
    this.filterFavoritesOnly = !this.filterFavoritesOnly;
  }

  isFavorited(item: AudiobookItem): boolean {
    const ownerName = this.authService.getOwnerName();
    return !!ownerName && (item.favorites ?? []).includes(ownerName);
  }

  toggleFavorite(item: AudiobookItem, event: Event): void {
    event.stopPropagation(); // don't also trigger openPlayer()
    const ownerName = this.authService.getOwnerName();
    if (!ownerName) return;

    const wasFavorited = this.isFavorited(item);
    const newValue = !wasFavorited;

    // Optimistic update
    item.favorites = newValue
      ? [...(item.favorites ?? []), ownerName]
      : (item.favorites ?? []).filter(o => o !== ownerName);

    this.api.setFavorite(item.id, newValue).subscribe({
      next: (resp) => {
        item.favorites = resp.favorites;
      },
      error: (err) => {
        this.logger.error('[AudiobooksComponent] toggleFavorite failed', err);
        // Revert on error
        item.favorites = wasFavorited
          ? [...(item.favorites ?? []), ownerName]
          : (item.favorites ?? []).filter(o => o !== ownerName);
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

  /** Toggle the mobile filter sheet */
  toggleSidebar(): void {
    this.sidebarCollapsed = !this.sidebarCollapsed;
  }

  toggleBulkEditMode(): void {
    this.bulkEditMode = !this.bulkEditMode;
    if (!this.bulkEditMode) {
      this.selectedForBulk.clear();
    }
  }

  toggleTileSelection(id: string): void {
    if (!this.bulkEditMode || !id) return;
    if (this.selectedForBulk.has(id)) {
      this.selectedForBulk.delete(id);
    } else {
      this.selectedForBulk.add(id);
    }
  }

  selectAllVisible(): void {
    if (!this.bulkEditMode) return;
    this.filteredItems.forEach(item => this.selectedForBulk.add(item.id));
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
      this.applyBulk(result);
    });
  }

  private applyBulk(result: MediaBulkEditDialogResult): void {
    const merge = (existing: string[] | undefined, incoming: string[]): string[] => {
      if (incoming.length === 0) return existing ?? [];
      if (result.mode === 'replace') return incoming;
      return Array.from(new Set([...(existing ?? []), ...incoming]));
    };

    const targets = this.items.filter(item => this.selectedForBulk.has(item.id));
    const failedTitles: string[] = [];
    for (const item of targets) {
      const owners = merge(item.owners, result.owners);
      const genres = merge(item.customGenres, result.genres);
      this.api.setMetadata(item.id, owners, genres).subscribe({
        next: () => {
          item.owners = owners;
          item.customGenres = genres;
        },
        error: (err) => {
          this.logger.error('[AudiobooksComponent] bulk setMetadata failed', err);
          failedTitles.push(titleOf(item));
          this.error = `Could not save changes for: ${failedTitles.join(', ')}`;
        }
      });
    }
    this.exitBulkEditMode();
  }

  titleLabel(item: AudiobookItem): string {
    return titleOf(item);
  }

  coverUrl(item: AudiobookItem): string {
    return item.id && hasCover(item) ? this.api.getCoverUrl(item.id, this.coverVersions.get(item.id)) : PLACEHOLDER_COVER;
  }

  durationLabel(item: AudiobookItem): string | undefined {
    return formatDuration(durationOf(item));
  }

  subtitleLabel(item: AudiobookItem): string {
    const parts = [authorOf(item), seriesOf(item)].filter((p): p is string => !!p);
    return parts.join(' · ');
  }

  openPlayer(item: AudiobookItem): void {
    // In bulk mode the whole tile acts as a selection target — popping the
    // player dialog mid-selection would just get in the way.
    if (this.bulkEditMode) {
      this.toggleTileSelection(item.id);
      return;
    }

    // The catalog list endpoint only returns lightweight summary data
    // (counts, not the actual audioFiles/chapters arrays) — the player needs
    // the full item detail to know what to actually stream.
    this.api.getItem(item.id).pipe(takeUntil(this.destroy$)).subscribe({
      next: (fullItem) => {
        // Preserve owners/customGenres/favorites from the list item, since
        // the detail endpoint's merge may not carry the exact same shape —
        // safer to just keep what's already known-good from the list view.
        const merged: AudiobookItem = { ...fullItem, owners: item.owners, customGenres: item.customGenres, favorites: item.favorites };
        this.dialog.open<AudiobookPlayerDialogComponent, AudiobookPlayerDialogData>(
          AudiobookPlayerDialogComponent,
          {
            width: '600px',
            maxWidth: '95vw',
            data: { item: merged }
          }
        );
      },
      error: (err) => {
        this.logger.error('[AudiobooksComponent] getItem failed', err);
        this.error = `Could not load "${titleOf(item)}" — please try again.`;
      }
    });
  }

  openEditDialog(item: AudiobookItem, event: Event): void {
    event.stopPropagation(); // don't also trigger openPlayer()

    const dialogData: MediaEditDialogData = {
      title: titleOf(item),
      genres: item.customGenres ?? [],
      owners: item.owners ?? [],
      availableGenres: this.genres,
      favoritedBy: item.favorites ?? [],
      mediaType: 'audiobook',
      id: item.id,
      coverUrl: this.coverUrl(item),
      author: authorOf(item) ?? null
    };

    const dialogRef = this.dialog.open<MediaEditDialogComponent, MediaEditDialogData, MediaEditDialogResult>(
      MediaEditDialogComponent,
      { width: '480px', data: dialogData }
    );

    dialogRef.afterClosed().subscribe(result => {
      // Deletion already happened inside the dialog (confirmDelete()) — nothing left
      // to save, just drop the item locally. Skip the favorite-sync below too, since
      // the item (and the favorite it just synced) no longer exists.
      if (result?.deleted) {
        this.items = this.items.filter(i => i.id !== item.id);
        return;
      }

      // The favorite toggle inside the dialog applies immediately (its own API call) —
      // sync whatever it ended up at back onto the item here, even on Cancel.
      item.favorites = dialogData.favoritedBy;

      if (!result) return;

      this.api.setMetadata(item.id, result.owners, result.genres, result.title).subscribe({
        next: () => {
          item.owners = result.owners;
          item.customGenres = result.genres;
          if (result.title && item.media?.metadata) {
            item.media.metadata.title = result.title;
          }
        },
        error: (err) => {
          this.logger.error('[AudiobooksComponent] setMetadata failed', err);
          this.error = `Could not save changes for "${titleOf(item)}" — please try again.`;
        }
      });

      if (result.coverUrl) {
        this.api.setCoverUrl(item.id, result.coverUrl).subscribe({
          next: () => {
            item.hasCustomCover = true;
            this.coverVersions.set(item.id, (this.coverVersions.get(item.id) ?? 0) + 1);
          },
          error: (err) => {
            this.logger.error('[AudiobooksComponent] setCoverUrl failed', err);
            this.error = `Could not save the new cover for "${titleOf(item)}" — please try again.`;
          }
        });
      }
    });
  }
}
