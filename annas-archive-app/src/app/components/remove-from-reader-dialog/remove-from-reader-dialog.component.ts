import { Component, Inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

export interface RemoveFromReaderDialogData {
  bookTitle: string;
}

export type RemoveFromReaderDialogResult = 'cancel' | 'remove';

@Component({
  selector: 'app-remove-from-reader-dialog',
  standalone: true,
  imports: [
    CommonModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule
  ],
  templateUrl: './remove-from-reader-dialog.component.html',
  styleUrls: ['./remove-from-reader-dialog.component.scss']
})
export class RemoveFromReaderDialogComponent {
  constructor(
    public dialogRef: MatDialogRef<RemoveFromReaderDialogComponent, RemoveFromReaderDialogResult>,
    @Inject(MAT_DIALOG_DATA) public data: RemoveFromReaderDialogData
  ) {}

  onRemove(): void {
    this.dialogRef.close('remove');
  }

  onCancel(): void {
    this.dialogRef.close('cancel');
  }
}
