import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatMenuModule } from '@angular/material/menu';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MediaLibraryApiService } from '../services/media-library-api.service';
import { MediaLookupResult } from '../services/media-search-api.service';
import {
  JellyfinPlayerModalComponent,
  JellyfinPlayerModalData
} from '../components/jellyfin-player-modal/jellyfin-player-modal.component';
import { ConfirmDialogComponent, ConfirmDialogData } from '../components/confirm-dialog/confirm-dialog.component';
import { LoggerService } from '../services/logger.service';

interface LibraryTile {
  result: MediaLookupResult;
  downloadedSeasonCount: number;
  totalSeasonCount: number;
}

type SortOrder = 'title' | 'year' | 'recent';
type TileSize = 'small' | 'medium' | 'large';

const PLACEHOLDER_POSTER = '/assets/placeholder.jpg';
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
  return (result['genres'] as string[] | undefined) ?? [];
}

function addedTimestamp(result: MediaLookupResult): number {
  return Date.parse((result['added'] as string) ?? '') || 0;
}

/**
 * Browse what's actually downloaded via Sonarr/Radarr — parallel to the
 * ebook Library page (search/genre/owner filters, tile-size + sort controls),
 * but backed by Sonarr/Radarr's own data instead of a local file scan (see
 * MediaLibraryEndpoints.cs for why: they already track file-existence
 * themselves). Ownership ("who requested this") is recorded server-side by
 * MediaOwnershipService, keyed by Sonarr/Radarr's own record ID rather than
 * tagging the media files, since Sonarr/Radarr reorganize/rename those on
 * import.
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
    MatMenuModule,
    MatDialogModule
  ],
  templateUrl: './media-library.component.html',
  styleUrl: './media-library.component.css'
})
export class MediaLibraryComponent implements OnInit {
  /** false = TV, true = Movies */
  showingMovies = false;
  loading = false;
  error: string | null = null;
  tvTiles: LibraryTile[] = [];
  movieTiles: MediaLookupResult[] = [];
  /** tmdbId of the movie currently resolving a watch URL, for a per-card spinner. */
  resolvingMovieId: number | null = null;

  searchTerm = '';
  selectedGenre = '';
  selectedOwners = new Set<string>();
  sortOrder: SortOrder = 'recent';
  tileSize: TileSize = 'medium';

  readonly owners = OWNERS;
  readonly ownerMenuOptions = [...OWNERS, UNASSIGNED];

  constructor(
    private api: MediaLibraryApiService,
    private dialog: MatDialog,
    private router: Router,
    private logger: LoggerService
  ) {}

  ngOnInit(): void {
    this.load();
  }

  toggleShowing(): void {
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
      const owner = result.owner || UNASSIGNED;
      if (!this.selectedOwners.has(owner)) return false;
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

  setTileSize(size: TileSize): void {
    this.tileSize = size;
  }

  resetView(): void {
    this.searchTerm = '';
    this.selectedGenre = '';
    this.selectedOwners.clear();
    this.sortOrder = 'recent';
    this.tileSize = 'medium';
  }

  openSeries(tile: LibraryTile): void {
    this.router.navigate(['/media-library/series', tile.result.id]);
  }

  posterUrl(result: MediaLookupResult): string {
    return posterUrlFor(result);
  }

  playMovie(movie: MediaLookupResult): void {
    if (movie.tmdbId === undefined || this.resolvingMovieId !== null) return;

    if (!movie['hasFile']) {
      this.error = `"${movie.title}" hasn't finished downloading yet.`;
      return;
    }

    this.error = null;
    this.resolvingMovieId = movie.tmdbId;
    this.api.watchMovie(movie.tmdbId).subscribe({
      next: (resp) => {
        this.resolvingMovieId = null;
        this.dialog.open<JellyfinPlayerModalComponent, JellyfinPlayerModalData>(JellyfinPlayerModalComponent, {
          width: '90vw',
          maxWidth: '1100px',
          data: { title: movie.title, embedUrl: resp.embedUrl }
        });
      },
      error: (err) => {
        this.resolvingMovieId = null;
        this.logger.error('[MediaLibraryComponent] watchMovie failed', err);
        this.error = `Jellyfin hasn't matched "${movie.title}" yet — it may still be scanning.`;
      }
    });
  }

  setSeriesOwner(tile: LibraryTile, owner: string, event: Event): void {
    event.stopPropagation();
    if (tile.result.id === undefined) return;
    const newOwner = owner === UNASSIGNED ? null : owner;
    this.api.setTvOwner(tile.result.id, newOwner).subscribe({
      next: () => { tile.result.owner = newOwner; },
      error: (err) => this.logger.error('[MediaLibraryComponent] setTvOwner failed', err)
    });
  }

  setMovieOwner(movie: MediaLookupResult, owner: string, event: Event): void {
    event.stopPropagation();
    if (movie.id === undefined) return;
    const newOwner = owner === UNASSIGNED ? null : owner;
    this.api.setMovieOwner(movie.id, newOwner).subscribe({
      next: () => { movie.owner = newOwner; },
      error: (err) => this.logger.error('[MediaLibraryComponent] setMovieOwner failed', err)
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
