import { Component, Inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MediaLibraryApiService } from '../../services/media-library-api.service';
import { AudiobookApiService } from '../../services/audiobook-api.service';
import { AuthService } from '../../services/auth.service';
import { LoggerService } from '../../services/logger.service';
import { FavoriteToggleComponent } from '../shared/favorite-toggle/favorite-toggle.component';
import { OwnerPickerComponent } from '../shared/owner-picker/owner-picker.component';
import { GenreChipsEditorComponent } from '../shared/genre-chips-editor/genre-chips-editor.component';
import { CoverPickerComponent } from '../shared/cover-picker/cover-picker.component';

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
  /** Audiobooks only — current cover (for the picker's preview) and author
   * (improves cover-search relevance). TV/movie posters come from Sonarr/Radarr
   * themselves, so there's nothing to override for those types. */
  coverUrl?: string | null;
  author?: string | null;
}

export interface MediaEditDialogResult {
  genres: string[];
  owners: string[];
  /** Audiobooks only — set when the user picked a new cover in this session. */
  coverUrl?: string;
  /** Audiobooks only — set when the user actually changed the title from what
   * Audiobookshelf reports. Omitted (not just empty) when unedited, so an
   * owners/genres-only save never touches a previously-set title override. */
  title?: string;
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
    FormsModule,
    MatDialogModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    FavoriteToggleComponent,
    OwnerPickerComponent,
    GenreChipsEditorComponent,
    CoverPickerComponent
  ],
  template: `
    <div class="media-edit-dialog">
      <h2 mat-dialog-title>{{ data.mediaType === 'audiobook' ? titleInput : data.title }}</h2>
      <div *ngIf="data.sizeLabel" class="size-label">{{ data.sizeLabel }} on disk</div>

      <div mat-dialog-content>
        <app-cover-picker
          *ngIf="data.mediaType === 'audiobook'"
          [title]="data.title"
          [author]="data.author ?? null"
          [currentCoverUrl]="data.coverUrl ?? null"
          (coverSelected)="selectedCoverUrl = $event"
        ></app-cover-picker>

        <mat-form-field *ngIf="data.mediaType === 'audiobook'" appearance="outline" class="title-field">
          <mat-label>Title</mat-label>
          <input matInput [(ngModel)]="titleInput" maxlength="500" />
        </mat-form-field>

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
    .title-field {
      width: 100%;
    }
  `]
})
export class MediaEditDialogComponent {
  genres: string[];
  selectedOwners: string[];
  selectedCoverUrl: string | undefined;
  titleInput: string;

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
    this.titleInput = data.title;
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
    const trimmedTitle = this.titleInput?.trim();
    const titleChanged = this.data.mediaType === 'audiobook' &&
      !!trimmedTitle &&
      trimmedTitle !== this.data.title;

    this.dialogRef.close({
      genres: this.genres,
      owners: this.selectedOwners,
      coverUrl: this.selectedCoverUrl,
      title: titleChanged ? trimmedTitle : undefined
    });
  }

  onCancel(): void {
    this.dialogRef.close();
  }
}
