import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
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

const PLACEHOLDER_POSTER = '/assets/placeholder.jpg';

/** Sonarr/Radarr's images array isn't guaranteed poster-first — it can lead
 * with a banner or fanart/background image instead, which is why picking
 * images[0] blindly (the original bug here) crops oddly when forced into a
 * portrait frame. Same fix as MediaResultCardComponent.posterUrl. */
function posterUrlFor(result: MediaLookupResult): string {
  const poster = result.images?.find((i: { coverType: string }) => i.coverType === 'poster');
  return poster?.remoteUrl || poster?.url || PLACEHOLDER_POSTER;
}

/**
 * Browse what's actually downloaded via Sonarr/Radarr — parallel to the
 * ebook Library page, but backed by Sonarr/Radarr's own data instead of a
 * local file scan (see MediaLibraryEndpoints.cs for why: they already
 * track file-existence themselves).
 */
@Component({
  selector: 'app-media-library',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatSlideToggleModule,
    MatProgressSpinnerModule,
    MatButtonModule,
    MatIconModule,
    MatTooltipModule,
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
