import { Injectable } from '@angular/core';
import { MatDialog, MatDialogRef } from '@angular/material/dialog';
import { LibraryApiService } from './library-api.service';
import { LoggerService } from './logger.service';
import { LibraryReviewModalComponent } from '../components/library-review-modal/library-review-modal.component';

/**
 * Opens the book-review modal (keep/delete → genre triage, 20 at a time) —
 * shared by every trigger point (the automatic once-a-day check, the top-nav
 * menu item, and the library sidebar's "Review Library" button) so there's
 * one place owning "don't stack a second dialog if one's already open"
 * instead of duplicating that guard per caller.
 */
@Injectable({ providedIn: 'root' })
export class LibraryReviewTriggerService {
  private dialogRef?: MatDialogRef<LibraryReviewModalComponent>;

  constructor(
    private dialog: MatDialog,
    private libraryApi: LibraryApiService,
    private logger: LoggerService
  ) {}

  /** Automatic on-login check — only opens if the once-a-day gate says it's due. */
  checkAndMaybeShow(): void {
    this.libraryApi.getLibraryReviewStatus().subscribe({
      next: (status) => {
        if (status.shouldShow) {
          this.open();
        }
      },
      error: (err) => this.logger.error('[LibraryReviewTrigger] Failed to check status', err)
    });
  }

  /** Manual trigger — bypasses the once-a-day gate. Safe to call repeatedly in
   *  the same day: the backend hands back a fresh batch of up to 20 more
   *  eligible books each time the previous batch has been fully decided,
   *  rather than re-showing an already-finished batch. */
  open(): void {
    if (this.dialogRef) return; // already open — never stack a second one

    this.libraryApi.startLibraryReviewSession().subscribe({
      next: (session) => {
        if (this.dialogRef) return;
        this.dialogRef = this.dialog.open(LibraryReviewModalComponent, {
          data: session,
          disableClose: true,
          width: '640px',
          maxWidth: '95vw'
        });
        this.dialogRef.afterClosed().subscribe(() => {
          this.dialogRef = undefined;
        });
      },
      error: (err) => this.logger.error('[LibraryReviewTrigger] Failed to start session', err)
    });
  }
}
