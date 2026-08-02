import { CommonModule } from '@angular/common';
import { Component, Inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import {
  AudiobookRequestApiService,
  AudiobookRequestPreview,
  AudiobookRequestResult
} from '../../services/audiobook-request-api.service';

@Component({
  selector: 'app-audiobook-request-confirm-dialog',
  standalone: true,
  imports: [CommonModule, MatButtonModule, MatDialogModule, MatIconModule, MatProgressSpinnerModule],
  templateUrl: './audiobook-request-confirm-dialog.component.html',
  styleUrl: './audiobook-request-confirm-dialog.component.scss'
})
export class AudiobookRequestConfirmDialogComponent {
  busy = false;
  error: string | null = null;

  constructor(
    @Inject(MAT_DIALOG_DATA) public data: AudiobookRequestPreview,
    private dialogRef: MatDialogRef<AudiobookRequestConfirmDialogComponent, AudiobookRequestResult | undefined>,
    private api: AudiobookRequestApiService
  ) {
    dialogRef.disableClose = true;
  }

  confirm(): void {
    if (this.busy) return;
    this.busy = true;
    this.error = null;
    this.api.confirmRequest(this.data.previewToken).subscribe({
      next: result => this.dialogRef.close(result),
      error: err => {
        this.busy = false;
        this.error = err?.error?.error || 'The audiobook request could not be confirmed.';
      }
    });
  }

  cancel(): void {
    if (!this.busy) this.dialogRef.close(undefined);
  }
}
