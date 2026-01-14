import { Component, Inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

export interface DeleteCacheDialogData {
  bookTitle: string;
  summaryCount: number;
  onExport: () => Promise<void>;
}

export type DeleteCacheDialogResult = 'cancel' | 'delete';

@Component({
  selector: 'app-delete-cache-dialog',
  standalone: true,
  imports: [
    CommonModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule
  ],
  templateUrl: './delete-cache-dialog.component.html',
  styleUrls: ['./delete-cache-dialog.component.scss']
})
export class DeleteCacheDialogComponent {
  exporting = false;
  exported = false;

  constructor(
    public dialogRef: MatDialogRef<DeleteCacheDialogComponent, DeleteCacheDialogResult>,
    @Inject(MAT_DIALOG_DATA) public data: DeleteCacheDialogData
  ) {}

  async onExport(): Promise<void> {
    this.exporting = true;
    try {
      await this.data.onExport();
      this.exported = true;
    } finally {
      this.exporting = false;
    }
  }

  onDelete(): void {
    this.dialogRef.close('delete');
  }

  onCancel(): void {
    this.dialogRef.close('cancel');
  }
}
