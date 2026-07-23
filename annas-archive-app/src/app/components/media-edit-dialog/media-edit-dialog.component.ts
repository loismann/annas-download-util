import { Component, Inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatDialog, MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatDividerModule } from '@angular/material/divider';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatChipsModule, MatChipInputEvent } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { COMMA, ENTER } from '@angular/cdk/keycodes';
import { CreateGenreDialogComponent } from '../create-genre-dialog/create-genre-dialog.component';

export interface MediaEditDialogData {
  title: string;
  genres: string[];
  owners: string[];
  /** Every genre tag already used anywhere in the media library, for the "Add a Genre" dropdown. */
  availableGenres: string[];
}

export interface MediaEditDialogResult {
  genres: string[];
  owners: string[];
}

const OWNERS = ['Paul', 'Mom', 'Dad'];

/**
 * Edit dialog for a downloaded show/movie's genre tags and owner(s) — the
 * media-library equivalent of BookEditDialogComponent, minus the book-only
 * concerns (cover picker, Kindle/Dropbox send, reader). Genres are free-form
 * user-created tags (reuses CreateGenreDialogComponent unchanged), same as
 * the ebook library; owners support multiple selections at once, unlike
 * books' single-owner tag, since more than one household member can watch
 * the same show/movie.
 */
@Component({
  selector: 'app-media-edit-dialog',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatSelectModule,
    MatChipsModule,
    MatIconModule,
    MatButtonModule,
    MatDividerModule
  ],
  template: `
    <div class="media-edit-dialog">
      <h2 mat-dialog-title>{{ data.title }}</h2>

      <div mat-dialog-content>
        <div class="section-label">Owners</div>
        <div class="owner-toggles">
          <button
            type="button"
            *ngFor="let owner of owners"
            class="owner-toggle"
            [class.active]="selectedOwners.has(owner)"
            (click)="toggleOwner(owner)"
          >
            {{ owner }}
          </button>
        </div>

        <mat-form-field appearance="outline" class="w-100 add-genre-field">
          <mat-label>Add a Genre</mat-label>
          <mat-select (selectionChange)="onGenreSelected($event.value)" [value]="null">
            <mat-option value="__create_new__" class="create-genre-option">
              <mat-icon>add_circle_outline</mat-icon>
              Would you like to create a new genre?
            </mat-option>
            <mat-divider></mat-divider>
            <mat-option *ngFor="let genre of availableGenreOptions" [value]="genre">
              {{ genre }}
            </mat-option>
          </mat-select>
        </mat-form-field>

        <mat-form-field appearance="outline" class="w-100">
          <mat-label>Genres</mat-label>
          <mat-chip-grid #chipGrid aria-label="Genres">
            <mat-chip-row *ngFor="let genre of genres" (removed)="removeGenre(genre)" [editable]="false">
              {{ genre }}
              <button matChipRemove [attr.aria-label]="'Remove ' + genre">
                <mat-icon>cancel</mat-icon>
              </button>
            </mat-chip-row>
          </mat-chip-grid>
          <input
            [matChipInputFor]="chipGrid"
            [matChipInputSeparatorKeyCodes]="separatorKeysCodes"
            (matChipInputTokenEnd)="addGenre($event)"
          />
        </mat-form-field>
      </div>

      <div mat-dialog-actions align="end">
        <button mat-stroked-button (click)="onCancel()">Cancel</button>
        <button mat-raised-button color="primary" (click)="onSave()">Save</button>
      </div>
    </div>
  `,
  styles: [`
    .media-edit-dialog { min-width: 420px; }
    .w-100 { width: 100%; }
    .section-label {
      font-size: 0.8rem;
      color: #64748b;
      margin-bottom: 6px;
    }
    .owner-toggles {
      display: flex;
      gap: 8px;
      margin-bottom: 20px;
    }
    .owner-toggle {
      border: 1px solid #cbd5f5;
      background: #ffffff;
      color: #3f51b5;
      padding: 6px 16px;
      border-radius: 999px;
      font-size: 0.85rem;
      cursor: pointer;
      transition: all 0.15s ease;
    }
    .owner-toggle.active {
      background: #3f51b5;
      color: #ffffff;
      border-color: #3f51b5;
    }
    .create-genre-option {
      color: #3f51b5;
      display: flex;
      align-items: center;
      gap: 6px;
    }
  `]
})
export class MediaEditDialogComponent {
  readonly separatorKeysCodes = [ENTER, COMMA] as const;
  readonly owners = OWNERS;

  genres: string[];
  selectedOwners: Set<string>;

  constructor(
    public dialogRef: MatDialogRef<MediaEditDialogComponent, MediaEditDialogResult>,
    @Inject(MAT_DIALOG_DATA) public data: MediaEditDialogData,
    private dialog: MatDialog
  ) {
    this.genres = [...(data.genres || [])];
    this.selectedOwners = new Set(data.owners || []);
  }

  get availableGenreOptions(): string[] {
    const genresLower = this.genres.map(g => g.toLowerCase());
    return (this.data.availableGenres || []).filter(g => !genresLower.includes(g.toLowerCase()));
  }

  toggleOwner(owner: string): void {
    if (this.selectedOwners.has(owner)) {
      this.selectedOwners.delete(owner);
    } else {
      this.selectedOwners.add(owner);
    }
  }

  addGenre(event: MatChipInputEvent): void {
    this.addGenreValue((event.value || '').trim());
    event.chipInput!.clear();
  }

  removeGenre(genre: string): void {
    this.genres = this.genres.filter(g => g !== genre);
  }

  onGenreSelected(value: string | null): void {
    if (!value) return;

    if (value === '__create_new__') {
      this.openCreateGenreDialog();
      return;
    }

    this.addGenreValue(value);
  }

  private openCreateGenreDialog(): void {
    const dialogRef = this.dialog.open<CreateGenreDialogComponent, unknown, string | null>(CreateGenreDialogComponent, {
      width: '400px',
      disableClose: false
    });

    dialogRef.afterClosed().subscribe(newGenre => {
      if (newGenre) {
        this.addGenreValue(newGenre);
      }
    });
  }

  private addGenreValue(value: string): void {
    if (!value) return;
    if (!this.genres.some(g => g.toLowerCase() === value.toLowerCase())) {
      this.genres.push(value);
    }
  }

  onSave(): void {
    this.dialogRef.close({
      genres: this.genres,
      owners: Array.from(this.selectedOwners)
    });
  }

  onCancel(): void {
    this.dialogRef.close();
  }
}
