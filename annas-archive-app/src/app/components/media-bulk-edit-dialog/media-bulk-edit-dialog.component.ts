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
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { COMMA, ENTER } from '@angular/cdk/keycodes';
import { CreateGenreDialogComponent } from '../create-genre-dialog/create-genre-dialog.component';

export interface MediaBulkEditDialogData {
  count: number;
  availableGenres: string[];
}

export interface MediaBulkEditDialogResult {
  genres: string[];
  owners: string[];
  mode: 'append' | 'replace';
}

const OWNERS = ['Paul', 'Mom', 'Dad'];

/**
 * Bulk genre/owner editor for the media library — same shape as the ebook
 * library's BulkEditDialogComponent (append-vs-replace toggle, blank starting
 * selections since selected items may already differ), applied to Sonarr
 * series / Radarr movies instead of books. Deliberately doesn't offer bulk
 * delete — that wasn't asked for, and per-item delete already exists.
 */
@Component({
  selector: 'app-media-bulk-edit-dialog',
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
    MatSlideToggleModule,
    MatDividerModule
  ],
  template: `
    <div class="media-bulk-edit-dialog">
      <h2 mat-dialog-title>Edit {{ data.count }} selected {{ data.count === 1 ? 'item' : 'items' }}</h2>

      <div mat-dialog-content>
        <div class="section-label">Owners to apply</div>
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
          <mat-label>Genres to apply</mat-label>
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

        <mat-slide-toggle [(ngModel)]="appendMode" class="append-toggle">
          {{ appendMode ? 'Append to existing owners/genres' : 'Replace existing owners/genres' }}
        </mat-slide-toggle>
      </div>

      <div mat-dialog-actions align="end">
        <button mat-stroked-button (click)="onCancel()">Cancel</button>
        <button mat-raised-button color="primary" (click)="onSave()">Apply</button>
      </div>
    </div>
  `,
  styles: [`
    .media-bulk-edit-dialog { min-width: 420px; }
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
    .append-toggle {
      display: block;
      margin-top: 12px;
    }
  `]
})
export class MediaBulkEditDialogComponent {
  readonly separatorKeysCodes = [ENTER, COMMA] as const;
  readonly owners = OWNERS;

  genres: string[] = [];
  selectedOwners = new Set<string>();
  appendMode = true;

  constructor(
    public dialogRef: MatDialogRef<MediaBulkEditDialogComponent, MediaBulkEditDialogResult>,
    @Inject(MAT_DIALOG_DATA) public data: MediaBulkEditDialogData,
    private dialog: MatDialog
  ) {}

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
      owners: Array.from(this.selectedOwners),
      mode: this.appendMode ? 'append' : 'replace'
    });
  }

  onCancel(): void {
    this.dialogRef.close();
  }
}
