import { Component, Inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';

import { BookDto } from '../../models/book-dto.model';

export interface BookSummaryModalData {
  book: BookDto;
  placeholderUrl: string;
}

/** Read-only "full summary" popup — opened by clicking a book card in the
 *  search results grid, since the card itself only shows a truncated
 *  description snippet. */
@Component({
  selector: 'app-book-summary-modal',
  standalone: true,
  imports: [CommonModule, MatDialogModule, MatButtonModule],
  templateUrl: './book-summary-modal.component.html',
  styleUrl: './book-summary-modal.component.css'
})
export class BookSummaryModalComponent {
  constructor(
    public dialogRef: MatDialogRef<BookSummaryModalComponent>,
    @Inject(MAT_DIALOG_DATA) public data: BookSummaryModalData
  ) {}

  get coverUrl(): string {
    return this.data.book.coverCandidates?.length ? this.data.book.coverCandidates[0] : this.data.placeholderUrl;
  }

  close(): void {
    this.dialogRef.close();
  }
}
