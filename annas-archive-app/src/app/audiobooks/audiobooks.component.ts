import { Component, OnInit } from '@angular/core';
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
import { MediaEditDialogComponent, MediaEditDialogData, MediaEditDialogResult } from '../components/media-edit-dialog/media-edit-dialog.component';
import {
  AudiobookPlayerDialogComponent,
  AudiobookPlayerDialogData
} from '../components/audiobook-player-dialog/audiobook-player-dialog.component';
import { LoggerService } from '../services/logger.service';
import { AuthService } from '../services/auth.service';

type SortOrder = 'title' | 'author' | 'recent';
type TileSize = 'small' | 'medium' | 'large';

const PLACEHOLDER_COVER = '/assets/placeholder.jpg';
/** The only three household members — same fixed set as the ebook/media libraries. */
const OWNERS = ['Paul', 'Mom', 'Dad'];

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
  return !!item.media?.coverPath;
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
    MatDialogModule
  ],
  templateUrl: './audiobooks.component.html',
  styleUrl: './audiobooks.component.css'
})
export class AudiobooksComponent implements OnInit {
  loading = false;
  error: string | null = null;
  items: AudiobookItem[] = [];

  searchTerm = '';
  selectedGenre = '';
  selectedOwners = new Set<string>();
  filterFavoritesOnly = false;
  sortOrder: SortOrder = 'recent';
  tileSize: TileSize = 'medium';

  readonly owners = OWNERS;

  constructor(
    private api: AudiobookApiService,
    private dialog: MatDialog,
    private logger: LoggerService,
    private authService: AuthService
  ) {}

  ngOnInit(): void {
    // Default to showing the current session's own audiobooks — same
    // convention as the ebook/media libraries.
    const ownerName = this.authService.getOwnerName();
    if (ownerName) {
      this.selectedOwners.add(ownerName);
    }

    this.load();
  }

  private load(): void {
    this.loading = true;
    this.error = null;

    this.api.getCatalog().subscribe({
      next: (items) => {
        this.items = items;
        this.loading = false;
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
    const term = this.searchTerm.trim().toLowerCase();
    if (term) {
      const haystack = `${titleOf(item)} ${authorOf(item) ?? ''} ${narratorOf(item) ?? ''} ${seriesOf(item) ?? ''}`.toLowerCase();
      if (!haystack.includes(term)) return false;
    }

    if (this.selectedGenre && !genresOf(item).includes(this.selectedGenre)) return false;

    if (this.selectedOwners.size > 0) {
      const itemOwners = item.owners ?? [];
      if (!itemOwners.some(o => this.selectedOwners.has(o))) return false;
    }

    // Favorites filter — cross-referenced against whichever owner filter buttons
    // are currently active, same convention as the ebook/media libraries; with no
    // owner filter active, anything favorited by any household member counts.
    if (this.filterFavoritesOnly) {
      const favorites = item.favorites ?? [];
      if (favorites.length === 0) return false;
      if (this.selectedOwners.size > 0 && !favorites.some(o => this.selectedOwners.has(o))) return false;
    }

    return true;
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
    if (this.selectedOwners.has(owner)) {
      this.selectedOwners.delete(owner);
    } else {
      this.selectedOwners.add(owner);
    }
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
  }

  titleLabel(item: AudiobookItem): string {
    return titleOf(item);
  }

  coverUrl(item: AudiobookItem): string {
    return item.id && hasCover(item) ? this.api.getCoverUrl(item.id) : PLACEHOLDER_COVER;
  }

  durationLabel(item: AudiobookItem): string | undefined {
    return formatDuration(durationOf(item));
  }

  subtitleLabel(item: AudiobookItem): string {
    const parts = [authorOf(item), seriesOf(item)].filter((p): p is string => !!p);
    return parts.join(' · ');
  }

  openPlayer(item: AudiobookItem): void {
    // The catalog list endpoint only returns lightweight summary data
    // (counts, not the actual audioFiles/chapters arrays) — the player needs
    // the full item detail to know what to actually stream.
    this.api.getItem(item.id).subscribe({
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
      id: item.id
    };

    const dialogRef = this.dialog.open<MediaEditDialogComponent, MediaEditDialogData, MediaEditDialogResult>(
      MediaEditDialogComponent,
      { width: '480px', data: dialogData }
    );

    dialogRef.afterClosed().subscribe(result => {
      // The favorite toggle inside the dialog applies immediately (its own API call) —
      // sync whatever it ended up at back onto the item here.
      item.favorites = dialogData.favoritedBy;

      if (!result) return;
      this.api.setMetadata(item.id, result.owners, result.genres).subscribe({
        next: () => {
          item.owners = result.owners;
          item.customGenres = result.genres;
        },
        error: (err) => {
          this.logger.error('[AudiobooksComponent] setMetadata failed', err);
          this.error = `Could not save changes for "${titleOf(item)}" — please try again.`;
        }
      });
    });
  }
}
