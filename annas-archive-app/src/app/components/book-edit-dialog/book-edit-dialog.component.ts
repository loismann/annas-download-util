import { Component, HostListener, Inject, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatInputModule } from '@angular/material/input';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { COMMA, ENTER } from '@angular/cdk/keycodes';
import { MatChipInputEvent } from '@angular/material/chips';
import { Router } from '@angular/router';
import { GenreMappingService } from '../../services/genre-mapping.service';
import { AnnaArchiveApiService } from '../../services/anna-archive-api.service';

export interface BookEditDialogData {
  title: string;
  authors: string[];
  primaryGenre: string | null;
  tags: string[];
  series: string | null;
  coverUrl?: string | null;
  availableGenres?: string[];
  fileName?: string | null;
  format?: string | null;
  canSendToKindle?: boolean;
  readerEnabled?: boolean | null;
  summary?: string | null;
}

export interface BookEditDialogResult {
  primaryGenre?: string;
  tags?: string[];
  series?: string | null;
  title?: string;
  authors?: string[];
  coverUrl?: string | null;
  deleted?: boolean;
}

interface CoverCandidate {
  url: string;
  width: number;
  height: number;
  ratio: number;
}

@Component({
  selector: 'app-book-edit-dialog',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatSelectModule,
    MatInputModule,
    MatChipsModule,
    MatIconModule,
    MatButtonModule,
    MatProgressSpinnerModule
  ],
  templateUrl: './book-edit-dialog.component.html',
  styleUrls: ['./book-edit-dialog.component.scss']
})
export class BookEditDialogComponent implements OnInit {
  readonly separatorKeysCodes = [ENTER, COMMA] as const;

  genres: string[];
  selectedGenre: string;
  tags: string[];
  series: string | null;
  title: string;
  authorsInput: string;
  placeholderUrl = '/assets/placeholder.jpg';
  selectedCoverUrl: string | null = null;
  manualCoverUrl = '';
  coverPickerOpen = false;
  coverCandidates: CoverCandidate[] = [];
  coverCandidatesLoading = false;
  coverCandidatesError: string | null = null;
  isDeleting = false;
  dadsKindleState: 'idle' | 'sending' | 'success' | 'error' = 'idle';
  momsKindleState: 'idle' | 'sending' | 'success' | 'error' = 'idle';
  dropboxState: 'idle' | 'sending' | 'success' | 'error' = 'idle';
  readerState: 'idle' | 'sending' | 'success' | 'error' = 'idle';

  // Summary state
  summary: string | null = null;
  summaryLoading = false;
  summarySource: string | null = null;

  constructor(
    public dialogRef: MatDialogRef<BookEditDialogComponent>,
    @Inject(MAT_DIALOG_DATA) public data: BookEditDialogData,
    private genreMappingService: GenreMappingService,
    private api: AnnaArchiveApiService,
    private router: Router
  ) {
    const fromLibrary = (data.availableGenres ?? []).filter(Boolean);
    this.genres = fromLibrary.length > 0
      ? [...fromLibrary]
      : [...genreMappingService.getStandardGenres()];
    if (!this.genres.includes('Uncategorized')) {
      this.genres.unshift('Uncategorized');
    }
    this.selectedGenre = data.primaryGenre || 'Uncategorized';
    this.tags = [...(data.tags || [])];
    this.series = data.series;
    this.title = data.title;
    this.authorsInput = (data.authors ?? []).join(', ');

    // Initialize summary from data if present
    this.summary = data.summary || null;
  }

  ngOnInit(): void {
    this.loadSummary();
  }

  private loadSummary(): void {
    if (!this.data.fileName) {
      return;
    }

    // If we already have a summary from the data, don't reload
    if (this.summary) {
      return;
    }

    this.summaryLoading = true;
    this.api.getLibraryBookSummary(this.data.fileName).subscribe({
      next: (resp) => {
        this.summary = resp.summary;
        this.summarySource = resp.source;
        this.summaryLoading = false;
      },
      error: () => {
        this.summaryLoading = false;
        this.summary = null;
      }
    });
  }

  @HostListener('document:keydown.enter', ['$event'])
  handleEnterKey(event: KeyboardEvent): void {
    if (this.isTagInput(event.target as HTMLElement)) {
      return;
    }
    event.preventDefault();
    this.onSave();
  }

  @HostListener('document:keydown.escape', ['$event'])
  handleEscapeKey(event: KeyboardEvent): void {
    event.preventDefault();
    this.onCancel();
  }

  addTag(event: MatChipInputEvent): void {
    const value = (event.value || '').trim();

    this.addTagValue(value);

    event.chipInput!.clear();
  }

  removeTag(tag: string): void {
    const index = this.tags.indexOf(tag);
    if (index >= 0) {
      this.tags.splice(index, 1);
    }
  }

  onSave(): void {
    const coverUrl = this.selectedCoverUrl && this.selectedCoverUrl !== this.data.coverUrl
      ? this.selectedCoverUrl
      : null;

    console.log('[BookEditDialog] Saving with:', {
      selectedCoverUrl: this.selectedCoverUrl,
      dataCoverUrl: this.data.coverUrl,
      willSendCoverUrl: coverUrl
    });

    this.dialogRef.close({
      primaryGenre: this.selectedGenre,
      tags: this.tags,
      series: this.series,
      title: this.title.trim() || this.data.title,
      authors: this.authorsInput
        .split(',')
        .map(author => author.trim())
        .filter(author => author.length > 0),
      coverUrl: coverUrl
    } as BookEditDialogResult);
  }

  onCancel(): void {
    this.dialogRef.close();
  }

  onCoverError(evt: Event): void {
    const img = evt.target as HTMLImageElement;
    if (!img || img.src.endsWith(this.placeholderUrl)) {
      return;
    }
    img.src = this.placeholderUrl;
  }

  get displayCoverUrl(): string {
    return this.selectedCoverUrl || this.data.coverUrl || this.placeholderUrl;
  }

  toggleCoverPicker(): void {
    this.coverPickerOpen = !this.coverPickerOpen;
    if (this.coverPickerOpen && this.coverCandidates.length === 0 && !this.coverCandidatesLoading) {
      this.loadCoverCandidates();
    }
  }

  refreshCoverCandidates(): void {
    this.coverCandidates = [];
    this.loadCoverCandidates();
  }

  selectCover(candidate: CoverCandidate): void {
    this.selectedCoverUrl = candidate.url;
  }

  applyManualCoverUrl(): void {
    const trimmed = this.manualCoverUrl.trim();
    if (!trimmed) return;
    this.selectedCoverUrl = trimmed;
    this.manualCoverUrl = '';
  }

  openGoogleImages(): void {
    const title = this.data.title?.trim();
    const author = this.data.authors?.[0]?.trim();
    let query = title || '';
    if (author) {
      query += ` ${author}`;
    }
    query += ' book cover';
    const searchUrl = `https://www.google.com/search?tbm=isch&q=${encodeURIComponent(query)}`;
    window.open(searchUrl, '_blank');
  }

  addSelectedGenreAsTag(): void {
    if (!this.selectedGenre || this.selectedGenre === 'Uncategorized') {
      return;
    }
    this.addTagValue(this.selectedGenre);
  }

  confirmDelete(): void {
    if (this.isDeleting || !this.data.fileName) {
      return;
    }

    const ok = window.confirm(`Delete "${this.data.title}" from the library? This cannot be undone.`);
    if (!ok) return;

    this.isDeleting = true;
    this.api.deleteLibraryBook(this.data.fileName).subscribe({
      next: () => {
        this.dialogRef.close({ deleted: true } as BookEditDialogResult);
      },
      error: () => {
        this.isDeleting = false;
        this.coverCandidatesError = 'Failed to delete book.';
      }
    });
  }

  enableReader(): void {
    if (!this.data.fileName || this.readerState === 'sending' || this.data.readerEnabled) {
      return;
    }

    this.readerState = 'sending';
    this.api.updateLibraryBookReaderEnabled(this.data.fileName, true).subscribe({
      next: (resp) => {
        this.data.readerEnabled = resp?.enabled ?? true;
        this.readerState = this.data.readerEnabled ? 'success' : 'error';
      },
      error: () => {
        this.readerState = 'error';
      }
    });
  }

  readerAction(): void {
    if (this.data.readerEnabled || this.readerState === 'success') {
      this.viewInReader();
      return;
    }
    this.enableReader();
  }

  viewInReader(): void {
    this.dialogRef.close();
    this.router.navigate(['/reader'], {
      queryParams: { fileName: this.data.fileName }
    });
  }

  sendToKindle(target: 'dad' | 'mom'): void {
    if (!this.data.fileName || this.dadsKindleState === 'sending' || this.momsKindleState === 'sending') {
      return;
    }
    if (!this.data.canSendToKindle) return;

    if (target === 'dad') {
      this.dadsKindleState = 'sending';
    } else {
      this.momsKindleState = 'sending';
    }

    this.api.sendLibraryToKindle(this.data.fileName, this.title, target).subscribe({
      next: (resp) => {
        const success = resp?.success ?? true;
        if (target === 'dad') {
          this.dadsKindleState = success ? 'success' : 'error';
        } else {
          this.momsKindleState = success ? 'success' : 'error';
        }
      },
      error: () => {
        if (target === 'dad') {
          this.dadsKindleState = 'error';
        } else {
          this.momsKindleState = 'error';
        }
      }
    });
  }

  sendToDropbox(): void {
    if (!this.data.fileName || this.dropboxState === 'sending') return;
    if (!this.data.canSendToKindle) return;

    this.dropboxState = 'sending';
    this.api.sendLibraryToKindle(this.data.fileName, this.title, 'dad', true).subscribe({
      next: (resp) => {
        const success = resp?.success ?? true;
        this.dropboxState = success ? 'success' : 'error';
      },
      error: () => {
        this.dropboxState = 'error';
      }
    });
  }

  private loadCoverCandidates(): void {
    const title = this.data.title?.trim();
    if (!title) {
      this.coverCandidatesError = 'Missing title for cover lookup.';
      return;
    }

    const author = this.data.authors?.[0];
    this.coverCandidatesLoading = true;
    this.coverCandidatesError = null;

    this.api.fetchLibraryCoverCandidates(title, author).subscribe({
      next: (resp) => {
        const urls = Array.from(new Set(resp.covers || []));
        this.applyCoverCandidates(urls)
          .catch(() => {
            this.coverCandidatesError = 'Failed to load cover images.';
          })
          .finally(() => {
            if (this.coverCandidates.length === 0 && !this.coverCandidatesError) {
              this.coverCandidatesError = 'No cover images found. Try Google Images or paste a URL manually.';
            }
            this.coverCandidatesLoading = false;
          });
      },
      error: () => {
        this.coverCandidatesLoading = false;
        this.coverCandidatesError = 'Cover lookup failed. Try Google Images or paste a URL manually.';
      }
    });
  }

  private async applyCoverCandidates(urls: string[]): Promise<void> {
    const validations = await Promise.all(urls.map((url) => this.validateCoverCandidate(url)));
    const candidates = validations.filter((candidate): candidate is CoverCandidate => candidate !== null);
    // Sort by size (larger images first)
    this.coverCandidates = candidates.sort((a, b) => (b.width * b.height) - (a.width * a.height));
  }

  private validateCoverCandidate(url: string): Promise<CoverCandidate | null> {
    return new Promise((resolve) => {
      const img = new Image();
      img.onload = () => {
        const width = img.naturalWidth || img.width;
        const height = img.naturalHeight || img.height;
        if (!width || !height) {
          resolve(null);
          return;
        }
        const ratio = height / width;
        // Accept all images that successfully load, no size or ratio restrictions
        resolve({ url, width, height, ratio });
      };
      img.onerror = () => resolve(null);
      img.src = url;
    });
  }

  private addTagValue(value: string): void {
    if (!value) {
      return;
    }
    const exists = this.tags.some(tag => tag.toLowerCase() === value.toLowerCase());
    if (!exists) {
      this.tags.push(value);
    }
  }

  private isTagInput(target: HTMLElement | null): boolean {
    if (!target) return false;
    if (target.tagName === 'TEXTAREA') return true;
    if (target.tagName === 'INPUT') {
      const placeholder = (target as HTMLInputElement).placeholder?.toLowerCase() || '';
      if (placeholder.includes('add tag')) return true;
    }
    return !!target.closest('mat-chip-grid');
  }
}
