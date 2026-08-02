import { CommonModule } from '@angular/common';
import { Component, Inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import {
  AudiobookRequestApiService,
  AudiobookSeriesConfirmResult,
  AudiobookSeriesMemberPreview,
  AudiobookSeriesPreview
} from '../../services/audiobook-request-api.service';

/**
 * Preview and capped confirmation for a whole series.
 *
 * The dialog shows the exact effect before anything happens: which books are
 * already playable, which are already requested, and which would actually be
 * added. Only the requestable ones are selectable, the count is stated on the
 * button, and the ceiling is enforced again on the server — this dialog is
 * the explanation, not the guarantee.
 */
@Component({
  selector: 'app-audiobook-series-request-dialog',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatButtonModule,
    MatCheckboxModule,
    MatDialogModule,
    MatIconModule,
    MatProgressSpinnerModule
  ],
  templateUrl: './audiobook-series-request-dialog.component.html',
  styleUrl: './audiobook-series-request-dialog.component.scss'
})
export class AudiobookSeriesRequestDialogComponent {
  selected: Record<string, boolean> = {};
  busy = false;
  error: string | null = null;
  result: AudiobookSeriesConfirmResult | null = null;

  constructor(
    @Inject(MAT_DIALOG_DATA) public data: AudiobookSeriesPreview,
    private dialogRef: MatDialogRef<AudiobookSeriesRequestDialogComponent, AudiobookSeriesConfirmResult | undefined>,
    private api: AudiobookRequestApiService
  ) {
    dialogRef.disableClose = true;
    // Pre-check every requestable book up to the ceiling, so the common case
    // is one confirmation and the ceiling is visible rather than surprising.
    this.data.members
      .filter(member => this.isSelectable(member))
      .slice(0, this.data.requestCeiling)
      .forEach(member => (this.selected[member.asin!] = true));
  }

  isSelectable(member: AudiobookSeriesMemberPreview): boolean {
    return member.classification === 'requestable' && !!member.asin;
  }

  get selectedAsins(): string[] {
    return Object.keys(this.selected).filter(asin => this.selected[asin]);
  }

  get overCeiling(): boolean {
    return this.selectedAsins.length > this.data.requestCeiling;
  }

  classificationLabel(member: AudiobookSeriesMemberPreview): string {
    return ({
      owned: 'In your library',
      requested: 'Already requested',
      requestable: 'Will be requested',
      ambiguous: 'Needs a manual search',
      unavailable: 'Not available'
    } as Record<string, string>)[member.classification] || member.classification;
  }

  confirm(): void {
    const asins = this.selectedAsins;
    if (!asins.length || this.busy) return;
    this.busy = true;
    this.error = null;
    this.api.confirmSeries(this.data.previewToken, asins, this.overCeiling).subscribe({
      next: result => {
        this.busy = false;
        this.result = result;
        // Kept open on partial failure so the per-book outcomes are readable.
        if (!result.failedCount) this.dialogRef.close(result);
      },
      error: err => {
        this.busy = false;
        this.error = err?.error?.error || 'The series request could not be completed.';
      }
    });
  }

  close(): void {
    if (!this.busy) this.dialogRef.close(this.result ?? undefined);
  }
}
