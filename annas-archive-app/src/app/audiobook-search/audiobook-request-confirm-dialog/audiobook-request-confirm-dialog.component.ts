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

  /** Naming the consequence on the button is the point — a book nothing carries
   *  should not be one OK click away from looking like a normal request. */
  get confirmLabel(): string {
    if (!this.data.releasesAvailable) return 'Add anyway to keep watching';
    return this.data.alreadyRequested ? 'Add me as requester' : 'Add monitored request';
  }

  confirm(): void {
    if (this.busy) return;
    this.busy = true;
    this.error = null;
    // The server refuses the confirm unless the no-releases warning comes back
    // acknowledged, so this flag has to mirror what the dialog actually showed.
    this.api.confirmRequest(this.data.previewToken, !this.data.releasesAvailable).subscribe({
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
