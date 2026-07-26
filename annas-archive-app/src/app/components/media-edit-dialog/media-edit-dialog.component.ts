import { Component, Inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MediaLibraryApiService } from '../../services/media-library-api.service';
import { AudiobookApiService } from '../../services/audiobook-api.service';
import { AuthService } from '../../services/auth.service';
import { LoggerService } from '../../services/logger.service';
import { FavoriteToggleComponent } from '../shared/favorite-toggle/favorite-toggle.component';
import { OwnerPickerComponent } from '../shared/owner-picker/owner-picker.component';
import { GenreChipsEditorComponent } from '../shared/genre-chips-editor/genre-chips-editor.component';

export interface MediaEditDialogData {
  title: string;
  genres: string[];
  owners: string[];
  /** Every genre tag already used anywhere in the media library, for the "Add a Genre" dropdown. */
  availableGenres: string[];
  /** Pre-formatted (e.g. "4.2 GB") — omitted/undefined when there's nothing on disk yet. */
  sizeLabel?: string;
  favoritedBy: string[];
  mediaType: 'tv' | 'movie' | 'audiobook';
  /** Sonarr/Radarr ids are integers; Audiobookshelf ids are string UUIDs. */
  id: number | string;
}

export interface MediaEditDialogResult {
  genres: string[];
  owners: string[];
}

/**
 * Edit dialog for a downloaded show/movie/audiobook's genre tags and owner(s) —
 * the media-library equivalent of BookEditDialogComponent, minus the book-only
 * concerns (cover picker, Kindle/Dropbox send, reader). Built from the shared
 * edit controls (favorite toggle, owner picker, genre chips), which own the
 * widgets while this dialog owns state and per-media-type persistence.
 */
@Component({
  selector: 'app-media-edit-dialog',
  standalone: true,
  imports: [
    CommonModule,
    MatDialogModule,
    MatButtonModule,
    FavoriteToggleComponent,
    OwnerPickerComponent,
    GenreChipsEditorComponent
  ],
  template: `
    <div class="media-edit-dialog">
      <h2 mat-dialog-title>{{ data.title }}</h2>
      <div *ngIf="data.sizeLabel" class="size-label">{{ data.sizeLabel }} on disk</div>

      <div mat-dialog-content>
        <app-favorite-toggle
          [favorited]="isFavorited"
          (toggled)="toggleFavorite($event)"
        ></app-favorite-toggle>

        <app-owner-picker
          [selected]="selectedOwners"
          (selectedChange)="selectedOwners = $event"
        ></app-owner-picker>

        <app-genre-chips-editor
          [values]="genres"
          [available]="data.availableGenres || []"
          (valuesChange)="genres = $event"
        ></app-genre-chips-editor>
      </div>

      <div mat-dialog-actions align="end">
        <button mat-stroked-button (click)="onCancel()">Cancel</button>
        <button mat-raised-button color="primary" (click)="onSave()">Save</button>
      </div>
    </div>
  `,
  styles: [`
    .media-edit-dialog { min-width: 420px; }
    .size-label {
      margin: -12px 0 16px;
      font-size: 0.8rem;
      color: #64748b;
    }
  `]
})
export class MediaEditDialogComponent {
  genres: string[];
  selectedOwners: string[];

  constructor(
    public dialogRef: MatDialogRef<MediaEditDialogComponent, MediaEditDialogResult>,
    @Inject(MAT_DIALOG_DATA) public data: MediaEditDialogData,
    private mediaApi: MediaLibraryApiService,
    private audiobookApi: AudiobookApiService,
    private authService: AuthService,
    private logger: LoggerService
  ) {
    this.genres = [...(data.genres || [])];
    this.selectedOwners = [...(data.owners || [])];
  }

  get isFavorited(): boolean {
    const ownerName = this.authService.getOwnerName();
    return !!ownerName && (this.data.favoritedBy ?? []).includes(ownerName);
  }

  toggleFavorite(newValue: boolean): void {
    const ownerName = this.authService.getOwnerName();
    if (!ownerName) return;

    // Optimistic update
    this.data.favoritedBy = newValue
      ? [...(this.data.favoritedBy ?? []), ownerName]
      : (this.data.favoritedBy ?? []).filter(o => o !== ownerName);

    const request$ = this.data.mediaType === 'movie'
      ? this.mediaApi.setMovieFavorite(this.data.id as number, newValue)
      : this.data.mediaType === 'audiobook'
      ? this.audiobookApi.setFavorite(this.data.id as string, newValue)
      : this.mediaApi.setTvFavorite(this.data.id as number, newValue);

    request$.subscribe({
      next: (resp) => {
        this.data.favoritedBy = resp.favorites;
      },
      error: (err) => {
        this.logger.error('[MediaEditDialog] Failed to update favorite:', err);
        // Revert on error
        this.data.favoritedBy = newValue
          ? (this.data.favoritedBy ?? []).filter(o => o !== ownerName)
          : [...(this.data.favoritedBy ?? []), ownerName];
      }
    });
  }

  onSave(): void {
    this.dialogRef.close({
      genres: this.genres,
      owners: this.selectedOwners
    });
  }

  onCancel(): void {
    this.dialogRef.close();
  }
}
