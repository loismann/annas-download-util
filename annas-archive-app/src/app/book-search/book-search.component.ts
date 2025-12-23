import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';

import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule }     from '@angular/material/input';
import { MatCheckboxModule }  from '@angular/material/checkbox';
import { MatSelectModule }    from '@angular/material/select';
import { MatButtonModule }    from '@angular/material/button';
import { MatCardModule }      from '@angular/material/card';

import {
  AnnaArchiveApiService,
  DownloadMemberResponse,
  SendToBooxResponse
} from '../services/anna-archive-api.service';

import { AuthService } from '../services/auth.service';
import { BookDto } from '../models/book-dto.model';
import { VERSION } from '../version';

@Component({
  selector: 'app-book-search',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatFormFieldModule,
    MatInputModule,
    MatCheckboxModule,
    MatSelectModule,
    MatButtonModule,
    MatCardModule,
  ],
  templateUrl: './book-search.component.html',
  styleUrls: ['./book-search.component.css'],
})
export class BookSearchComponent {
  placeholderUrl = '/assets/placeholder.jpg';
  buildTime = VERSION.buildTime;

  /* ───────── search form state ───────── */
  searchTerm = '';
  exact = false;

  /* ───────── ui state ───────── */
  loading = false;
  error: string | null = null;
  searchPerformed = false;

  books: BookDto[] = [];
  selectedFormat = '';

  downloadsLeft: number | null = null;

  constructor(
    private api: AnnaArchiveApiService,
    public authService: AuthService
  ) {}

  /* ───────── helpers for template ───────── */
  get availableFormats(): string[] {
    return Array.from(new Set(this.books.map(b => b.format))).sort();
  }

  get filteredBooks(): BookDto[] {
    return this.selectedFormat
      ? this.books.filter(b => b.format === this.selectedFormat)
      : this.books;
  }

  /* ───────── search submit ───────── */
  onSearch(): void {
    this.error = null;
    if (!this.searchTerm.trim()) {
      this.error = 'Please enter a search term.';
      return;
    }

    this.loading = true;
    this.searchPerformed = true;
    this.selectedFormat = '';
    this.downloadsLeft = null;

    this.api.searchBooks(this.searchTerm.trim(), this.exact).subscribe({
      next: books => {
        this.books = books;
        this.books.forEach(b => {
          b.sendState = 'idle';
          b.dadsKindleState = 'idle';
          b.momsKindleState = 'idle';
        });
        this.loading = false;
      },
      error: err => {
        this.error = 'Error fetching books.';
        console.error(err);
        this.loading = false;
      },
    });
  }

  /* ───────── download button ───────── */
  download(book: BookDto): void {
    this.api.downloadMember(book.md5).subscribe({
      next: (resp: DownloadMemberResponse) => {
        this.downloadsLeft = resp.accountFastInfo?.downloadsLeft ?? null;
        window.open(resp.downloadUrl, '_blank', 'noopener');
      },
      error: err => {
        console.error('Download failed', err);
        this.error = 'Download failed.';
      }
    });
  }

  /* ───────── send-to-boox button (via Dropbox) ───────── */
  sendToBoox(book: BookDto): void {
    if (book.sendState === 'sending') return;  // guard double-click
    book.sendState = 'sending';

    this.api.sendToBoox(book.md5, book.title).subscribe({
      next: (resp: SendToBooxResponse) => {
        this.downloadsLeft =
          resp.accountFastInfo?.downloadsLeft ?? this.downloadsLeft;
        book.sendState = resp.success ? 'success' : 'error';
      },
      error: err => {
        console.error('Send-to-Boox failed', err);
        book.sendState = 'error';
      }
    });
  }

  /* ───────── send-to-dad's-kindle button ───────── */
  sendToDadsKindle(book: BookDto): void {
    if (book.dadsKindleState === 'sending') return;  // guard double-click
    book.dadsKindleState = 'sending';

    this.api.sendToKindle(book.md5, book.title, 'dad').subscribe({
      next: (resp: SendToBooxResponse) => {
        this.downloadsLeft =
          resp.accountFastInfo?.downloadsLeft ?? this.downloadsLeft;
        book.dadsKindleState = resp.success ? 'success' : 'error';
      },
      error: err => {
        console.error('Send-to-Dad\'s-Kindle failed', err);
        book.dadsKindleState = 'error';
      }
    });
  }

  /* ───────── send-to-mom's-kindle button ───────── */
  sendToMomsKindle(book: BookDto): void {
    if (book.momsKindleState === 'sending') return;  // guard double-click
    book.momsKindleState = 'sending';

    this.api.sendToKindle(book.md5, book.title, 'mom').subscribe({
      next: (resp: SendToBooxResponse) => {
        this.downloadsLeft =
          resp.accountFastInfo?.downloadsLeft ?? this.downloadsLeft;
        book.momsKindleState = resp.success ? 'success' : 'error';
      },
      error: err => {
        console.error('Send-to-Mom\'s-Kindle failed', err);
        book.momsKindleState = 'error';
      }
    });
  }

  onCoverError(book: BookDto, evt: Event): void {
      const img = evt.target as HTMLImageElement;

      // if we're already showing the placeholder, do nothing  
      if (img.src.endsWith(this.placeholderUrl)) {
        return;
      }

      // if there are more candidates, try the next  
      if (book.coverCandidates.length > 1) {
        book.coverCandidates.shift();
        img.src = book.coverCandidates[0];
      } else {
        // no more external covers → fall back  
        img.src = this.placeholderUrl;
      }
    }
}
