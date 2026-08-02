import { CommonModule } from '@angular/common';
import { Component, Inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatRadioModule } from '@angular/material/radio';
import {
  AudiobookReleaseGrabResult,
  AudiobookReleaseSearchResponse,
  AudiobookRequestApiService
} from '../../services/audiobook-request-api.service';

@Component({
  selector: 'app-audiobook-release-picker',
  standalone: true,
  imports: [CommonModule, FormsModule, MatButtonModule, MatDialogModule, MatIconModule, MatProgressSpinnerModule, MatRadioModule],
  templateUrl: './audiobook-release-picker.component.html',
  styleUrl: './audiobook-release-picker.component.scss'
})
export class AudiobookReleasePickerComponent {
  selectedToken: string | null = null;
  busy = false;
  error: string | null = null;

  constructor(
    @Inject(MAT_DIALOG_DATA) public data: AudiobookReleaseSearchResponse,
    private dialogRef: MatDialogRef<AudiobookReleasePickerComponent, AudiobookReleaseGrabResult | undefined>,
    private api: AudiobookRequestApiService
  ) {
    dialogRef.disableClose = true;
  }

  grab(): void {
    if (!this.selectedToken || this.busy) return;
    this.busy = true;
    this.error = null;
    this.api.grabRelease(this.data.listenarrId, this.selectedToken).subscribe({
      next: result => this.dialogRef.close(result),
      error: err => {
        this.busy = false;
        this.error = err?.error?.error || 'The selected release could not be sent to the download client.';
      }
    });
  }

  cancel(): void {
    if (!this.busy) this.dialogRef.close(undefined);
  }

  sizeLabel(bytes: number): string {
    if (!bytes || bytes < 1) return 'Size unknown';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    const index = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
    return `${(bytes / Math.pow(1024, index)).toFixed(index > 2 ? 1 : 0)} ${units[index]}`;
  }
}
