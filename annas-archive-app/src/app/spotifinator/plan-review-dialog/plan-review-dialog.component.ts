import { Component, Inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { SpotifinatorApiService } from '../../services/spotifinator-api.service';
import { SpotifyPlan } from '../spotifinator.models';

/**
 * The last thing between a sentence and a real change to someone's Spotify.
 *
 * This used to be a card in the chat transcript, which had two problems. Pressing
 * "Create in Spotify" on a draft scrolled a review card into a *different* pane —
 * nothing on screen said the decision had moved, so the button looked like it had
 * done nothing. And once confirmed, the card stayed, so the transcript filled with
 * expanded plans nobody needed to read again.
 *
 * A modal fixes both: it is unmissable, it is the only thing that can be interacted
 * with while it is open, and when it closes it leaves one line behind instead of a
 * panel. It resolves with the executed plan, or `undefined` if the change was
 * abandoned — the caller does not have to distinguish "cancelled" from "dismissed",
 * because in both cases nothing happened.
 */
@Component({
  selector: 'app-plan-review-dialog',
  standalone: true,
  imports: [
    CommonModule, MatDialogModule, MatButtonModule, MatIconModule, MatProgressSpinnerModule
  ],
  templateUrl: './plan-review-dialog.component.html',
  styleUrls: ['./plan-review-dialog.component.scss']
})
export class PlanReviewDialogComponent {
  plan: SpotifyPlan;

  /** Set once the tick box is ticked. High-impact plans cannot confirm without it. */
  acknowledged = false;

  /** Blocks both buttons while a request is in flight, so a double click cannot
   *  confirm and cancel the same plan. */
  busy = false;

  error: string | null = null;

  constructor(
    private dialogRef: MatDialogRef<PlanReviewDialogComponent, SpotifyPlan | undefined>,
    private api: SpotifinatorApiService,
    @Inject(MAT_DIALOG_DATA) data: { plan: SpotifyPlan }
  ) {
    this.plan = data.plan;

    // A change this size should cost a deliberate press of Escape, not an
    // accidental click on the backdrop.
    dialogRef.disableClose = true;
  }

  get needsAcknowledgement(): boolean {
    return this.plan.preview.requiresHighImpactAcknowledgement;
  }

  get canConfirm(): boolean {
    return !this.busy && (!this.needsAcknowledgement || this.acknowledged);
  }

  confirm(): void {
    if (!this.canConfirm) return;

    this.busy = true;
    this.error = null;

    this.api.confirmPlan(this.plan.id, this.acknowledged).subscribe({
      next: executed => this.dialogRef.close(executed),
      error: err => {
        this.busy = false;
        // A 409 carries the real sentence — expired, or the playlist moved under
        // us. It is shown here rather than closing, because the plan is dead and
        // saying so beside the button that failed is where it will be read.
        this.error = err.error?.error || 'That change could not be applied.';
      }
    });
  }

  /**
   * Cancels server-side rather than just closing, so the plan is recorded as
   * abandoned instead of sitting in the store waiting to expire.
   */
  cancel(): void {
    if (this.busy) return;

    this.busy = true;
    this.api.cancelPlan(this.plan.id).subscribe({
      next: () => this.dialogRef.close(undefined),
      // Even if the cancel call fails the user's intent was to stop, so the dialog
      // closes either way; the plan expires on its own.
      error: () => this.dialogRef.close(undefined)
    });
  }
}
