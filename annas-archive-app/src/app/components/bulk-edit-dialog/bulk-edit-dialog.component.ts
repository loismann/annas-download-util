import { Component, Inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogRef, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatChipsModule } from '@angular/material/chips';
import { MatChipInputEvent } from '@angular/material/chips';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { COMMA, ENTER } from '@angular/cdk/keycodes';
import { OwnerPickerComponent } from '../shared/owner-picker/owner-picker.component';
import { GenreChipsEditorComponent } from '../shared/genre-chips-editor/genre-chips-editor.component';
import { BOOK_OWNER_OPTIONS } from '../../constants/owners';

export interface BookBulkEditDialogData {
  bookFileNames: string[];
  bookTitles: string[];
  availableGenres?: string[];
}

export interface BookBulkEditDialogResult {
  authors?: string[];
  primaryGenre?: string;
  tags?: string[];
  tagsMode?: 'append' | 'replace';
  series?: string | null;
  deleted?: boolean;
  favoriteAll?: boolean;
  /** Empty/omitted = leave each book's existing owners untouched. Unlike
   * genre tags, owners are always a replace when present — "set the owner to
   * Mom" across a batch of books means "these are Mom's now," not "add Mom
   * alongside whoever's already on each one." (Adding an owner without
   * displacing the existing one is what the single-book edit dialog is for,
   * where you can see and consciously keep the current owner.) */
  owners?: string[];
}

@Component({
  selector: 'app-bulk-edit-dialog',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatButtonModule,
    MatIconModule,
    MatChipsModule,
    MatSlideToggleModule,
    OwnerPickerComponent,
    GenreChipsEditorComponent
  ],
  templateUrl: './bulk-edit-dialog.component.html',
  styleUrl: './bulk-edit-dialog.component.scss'
})
export class BulkEditDialogComponent {
  authorsInput = '';
  selectedGenre = '';
  series = '';
  tags: string[] = [];
  genres: string[] = [];
  appendTags = true; // Default to append mode
  readonly separatorKeysCodes = [ENTER, COMMA] as const;

  readonly ownerOptions = BOOK_OWNER_OPTIONS;
  selectedOwners = new Set<string>();

  constructor(
    public dialogRef: MatDialogRef<BulkEditDialogComponent>,
    @Inject(MAT_DIALOG_DATA) public data: BookBulkEditDialogData
  ) {
    this.genres = data.availableGenres ?? [];
  }

  /** Array view of the owner-tag selection for the shared owner picker. */
  get selectedOwnersList(): string[] {
    return Array.from(this.selectedOwners);
  }

  onOwnersChange(values: string[]): void {
    this.selectedOwners = new Set(values);
  }

  addTag(event: MatChipInputEvent): void {
    const value = (event.value || '').trim();
    if (value) {
      if (!this.tags.includes(value)) {
        this.tags.push(value);
      }
    }
    event.chipInput!.clear();
  }

  removeTag(tag: string): void {
    const index = this.tags.indexOf(tag);
    if (index >= 0) {
      this.tags.splice(index, 1);
    }
  }

  addSelectedGenreAsTag(): void {
    if (!this.selectedGenre || this.selectedGenre === 'Uncategorized') return;
    if (!this.tags.includes(this.selectedGenre)) {
      this.tags.push(this.selectedGenre);
    }
  }

  toggleOwner(tag: string): void {
    if (this.selectedOwners.has(tag)) {
      this.selectedOwners.delete(tag);
    } else {
      this.selectedOwners.add(tag);
    }
  }

  onCancel(): void {
    this.dialogRef.close();
  }

  onSave(): void {
    const result: BookBulkEditDialogResult = {};

    if (this.authorsInput.trim()) {
      result.authors = this.authorsInput
        .split(',')
        .map(author => author.trim())
        .filter(author => author.length > 0);
    }

    if (this.selectedGenre) {
      result.primaryGenre = this.selectedGenre;
    }

    if (this.tags.length > 0) {
      result.tags = this.tags;
      result.tagsMode = this.appendTags ? 'append' : 'replace';
    }

    if (this.series.trim()) {
      result.series = this.series.trim();
    }

    if (this.selectedOwners.size > 0) {
      result.owners = Array.from(this.selectedOwners);
    }

    this.dialogRef.close(result);
  }

  onDeleteAll(): void {
    const count = this.data.bookFileNames.length;
    const firstConfirm = window.confirm(
      `Are you sure you want to DELETE ${count} book${count === 1 ? '' : 's'}?\n\nThis action CANNOT be undone!`
    );

    if (!firstConfirm) return;

    const secondConfirm = window.confirm(
      `FINAL WARNING: You are about to permanently delete ${count} book${count === 1 ? '' : 's'}.\n\nAre you absolutely sure?`
    );

    if (!secondConfirm) return;

    this.dialogRef.close({ deleted: true });
  }

  onFavoriteAll(): void {
    this.dialogRef.close({ favoriteAll: true });
  }
}
