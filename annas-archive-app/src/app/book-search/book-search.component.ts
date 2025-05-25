import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule }  from '@angular/forms';

import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule }     from '@angular/material/input';
import { MatCheckboxModule }  from '@angular/material/checkbox';
import { MatSelectModule }    from '@angular/material/select';
import { MatButtonModule }    from '@angular/material/button';
import { MatCardModule }      from '@angular/material/card';

import { AnnaArchiveApiService, DownloadMemberResponse } from '../services/anna-archive-api.service';
import { BookDto }                                     from '../models/book-dto.model';

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
  searchTerm = '';
  exact = false;

  loading = false;
  error: string | null = null;
  searchPerformed = false;

  books: BookDto[] = [];
  selectedFormat = '';

  downloadsLeft: number | null = null;

  constructor(private api: AnnaArchiveApiService) {}

  get availableFormats(): string[] {
    return Array.from(new Set(this.books.map(b => b.format))).sort();
  }

  get filteredBooks(): BookDto[] {
    return this.selectedFormat
      ? this.books.filter(b => b.format === this.selectedFormat)
      : this.books;
  }

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
        this.loading = false;
      },
      error: err => {
        this.error = 'Error fetching books.';
        console.error(err);
        this.loading = false;
      },
    });
  }

  download(book: BookDto) {
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

  tryNextCover(book: BookDto, evt: Event) {
    book.coverCandidates.shift();
    const img = evt.target as HTMLImageElement;
    if (book.coverCandidates.length) {
      img.src = book.coverCandidates[0];
    } else {
      img.style.display = 'none';
    }
  }
}
