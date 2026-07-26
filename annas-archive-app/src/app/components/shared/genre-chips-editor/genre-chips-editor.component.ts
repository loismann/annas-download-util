import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatDividerModule } from '@angular/material/divider';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatChipsModule, MatChipInputEvent } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { COMMA, ENTER } from '@angular/cdk/keycodes';
import { CreateGenreDialogComponent } from '../../create-genre-dialog/create-genre-dialog.component';

/**
 * The "Add a Genre" dropdown (with the create-new-genre flow) plus the
 * removable chip grid with free-text input — previously duplicated across
 * the book, media, video, and both bulk edit dialogs. The parent owns the
 * list; this emits the full updated list on every change.
 *
 * Also used for books' tag editing (set [label]/[addLabel]) — same widget,
 * different words.
 */
@Component({
  selector: 'app-genre-chips-editor',
  standalone: true,
  imports: [
    CommonModule,
    MatDialogModule,
    MatDividerModule,
    MatFormFieldModule,
    MatSelectModule,
    MatChipsModule,
    MatIconModule
  ],
  template: `
    <mat-form-field *ngIf="showAddDropdown" appearance="outline" class="w-100 add-genre-field">
      <mat-label>{{ addLabel }}</mat-label>
      <mat-select (selectionChange)="onOptionSelected($event.value)" [value]="null">
        <mat-option *ngIf="allowCreate" value="__create_new__" class="create-genre-option">
          <mat-icon>add_circle_outline</mat-icon>
          Would you like to create a new genre?
        </mat-option>
        <mat-divider *ngIf="allowCreate"></mat-divider>
        <mat-option *ngFor="let option of availableOptions" [value]="option">
          {{ option }}
        </mat-option>
      </mat-select>
    </mat-form-field>

    <mat-form-field appearance="outline" class="w-100">
      <mat-label>{{ label }}</mat-label>
      <mat-chip-grid #chipGrid [attr.aria-label]="label">
        <mat-chip-row *ngFor="let value of values" (removed)="remove(value)" [editable]="false">
          {{ value }}
          <button matChipRemove [attr.aria-label]="'Remove ' + value">
            <mat-icon>cancel</mat-icon>
          </button>
        </mat-chip-row>
      </mat-chip-grid>
      <input
        [matChipInputFor]="chipGrid"
        [matChipInputSeparatorKeyCodes]="separatorKeysCodes"
        (matChipInputTokenEnd)="addFromInput($event)"
      />
    </mat-form-field>
  `,
  styles: [`
    .w-100 { width: 100%; }
    .create-genre-option {
      color: #3f51b5;
      display: flex;
      align-items: center;
      gap: 6px;
    }
  `]
})
export class GenreChipsEditorComponent {
  readonly separatorKeysCodes = [ENTER, COMMA] as const;

  @Input() values: string[] = [];
  @Input() available: string[] = [];
  @Input() label = 'Genres';
  @Input() addLabel = 'Add a Genre';
  @Input() allowCreate = true;
  /** Hide the add-dropdown entirely (chip grid + free-text input only) — used by
   * flows that provide their own way to add values, like the bulk dialogs. */
  @Input() showAddDropdown = true;
  @Output() valuesChange = new EventEmitter<string[]>();

  constructor(private dialog: MatDialog) {}

  get availableOptions(): string[] {
    const currentLower = this.values.map(v => v.toLowerCase());
    return (this.available || []).filter(o => !currentLower.includes(o.toLowerCase()));
  }

  onOptionSelected(value: string | null): void {
    if (!value) return;

    if (value === '__create_new__') {
      this.openCreateGenreDialog();
      return;
    }

    this.addValue(value);
  }

  addFromInput(event: MatChipInputEvent): void {
    this.addValue((event.value || '').trim());
    event.chipInput!.clear();
  }

  remove(value: string): void {
    this.values = this.values.filter(v => v !== value);
    this.valuesChange.emit(this.values);
  }

  private openCreateGenreDialog(): void {
    const dialogRef = this.dialog.open<CreateGenreDialogComponent, unknown, string | null>(CreateGenreDialogComponent, {
      width: '400px',
      disableClose: false
    });

    dialogRef.afterClosed().subscribe(newGenre => {
      if (newGenre) {
        this.addValue(newGenre);
      }
    });
  }

  private addValue(value: string): void {
    if (!value) return;
    if (!this.values.some(v => v.toLowerCase() === value.toLowerCase())) {
      this.values = [...this.values, value];
      this.valuesChange.emit(this.values);
    }
  }
}
