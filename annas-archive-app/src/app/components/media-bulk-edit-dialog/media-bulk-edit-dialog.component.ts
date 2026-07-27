import { Component, Inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { OwnerPickerComponent } from '../shared/owner-picker/owner-picker.component';
import { GenreChipsEditorComponent } from '../shared/genre-chips-editor/genre-chips-editor.component';

export interface MediaBulkEditDialogData {
  count: number;
  availableGenres: string[];
}

export interface MediaBulkEditDialogResult {
  genres: string[];
  owners: string[];
  mode: 'append' | 'replace';
}

/**
 * Bulk genre/owner editor for the media library — same shape as the ebook
 * library's BulkEditDialogComponent (append-vs-replace toggle, blank starting
 * selections since selected items may already differ), applied to Sonarr
 * series / Radarr movies instead of books. Deliberately doesn't offer bulk
 * delete — that wasn't asked for, and per-item delete already exists.
 * Widgets come from the shared edit controls.
 */
@Component({
  selector: 'app-media-bulk-edit-dialog',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatDialogModule,
    MatButtonModule,
    MatSlideToggleModule,
    OwnerPickerComponent,
    GenreChipsEditorComponent
  ],
  template: `
    <div class="media-bulk-edit-dialog">
      <h2 mat-dialog-title>Edit {{ data.count }} selected {{ data.count === 1 ? 'item' : 'items' }}</h2>

      <div mat-dialog-content>
        <app-owner-picker
          label="Owners to apply"
          [selected]="selectedOwners"
          (selectedChange)="selectedOwners = $event"
        ></app-owner-picker>

        <app-genre-chips-editor
          label="Genres to apply"
          [values]="genres"
          [available]="data.availableGenres || []"
          (valuesChange)="genres = $event"
        ></app-genre-chips-editor>

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
    .media-bulk-edit-dialog { min-width: min(420px, calc(100vw - 80px)); }
    .append-toggle {
      display: block;
      margin-top: 12px;
    }
  `]
})
export class MediaBulkEditDialogComponent {
  genres: string[] = [];
  selectedOwners: string[] = [];
  appendMode = true;

  constructor(
    public dialogRef: MatDialogRef<MediaBulkEditDialogComponent, MediaBulkEditDialogResult>,
    @Inject(MAT_DIALOG_DATA) public data: MediaBulkEditDialogData
  ) {}

  onSave(): void {
    this.dialogRef.close({
      genres: this.genres,
      owners: this.selectedOwners,
      mode: this.appendMode ? 'append' : 'replace'
    });
  }

  onCancel(): void {
    this.dialogRef.close();
  }
}
