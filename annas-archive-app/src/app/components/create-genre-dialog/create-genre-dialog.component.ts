import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

@Component({
  selector: 'app-create-genre-dialog',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatIconModule
  ],
  template: `
    <div class="create-genre-dialog">
      <div class="dialog-icon">
        <mat-icon>category</mat-icon>
      </div>
      <h2 class="dialog-title">Create New Genre</h2>
      <div class="dialog-content">
        <mat-form-field appearance="outline" class="w-100">
          <mat-label>Genre Name</mat-label>
          <input
            matInput
            [(ngModel)]="genreName"
            (keydown.enter)="onSubmit()"
            autofocus
          />
        </mat-form-field>
      </div>
      <div class="dialog-actions">
        <button mat-stroked-button class="cancel-button" (click)="onCancel()">Cancel</button>
        <button
          mat-raised-button
          color="primary"
          class="create-button"
          (click)="onSubmit()"
          [disabled]="!genreName?.trim()"
        >
          <mat-icon>add</mat-icon>
          Create Genre
        </button>
      </div>
    </div>
  `,
  styles: [`
    .create-genre-dialog {
      padding: 24px;
      max-width: 400px;
    }

    .dialog-icon {
      display: flex;
      justify-content: center;
      margin-bottom: 16px;

      mat-icon {
        font-size: 48px;
        width: 48px;
        height: 48px;
        color: #3f51b5;
      }
    }

    .dialog-title {
      text-align: center;
      margin: 0 0 20px;
      font-size: 1.5rem;
      font-weight: 600;
      color: #333;
    }

    .dialog-content {
      margin-bottom: 24px;
    }

    .w-100 {
      width: 100%;
    }

    .dialog-actions {
      display: flex;
      justify-content: flex-end;
      gap: 12px;
    }

    .cancel-button {
      color: #666;
    }

    .create-button {
      display: flex;
      align-items: center;
      gap: 4px;

      mat-icon {
        font-size: 18px;
        width: 18px;
        height: 18px;
      }
    }
  `]
})
export class CreateGenreDialogComponent {
  genreName = '';

  constructor(public dialogRef: MatDialogRef<CreateGenreDialogComponent>) {}

  onSubmit(): void {
    const trimmed = this.genreName?.trim();
    if (trimmed) {
      this.dialogRef.close(trimmed);
    }
  }

  onCancel(): void {
    this.dialogRef.close(null);
  }
}
