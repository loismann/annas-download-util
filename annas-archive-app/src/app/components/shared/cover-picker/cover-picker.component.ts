import { Component, EventEmitter, Input, OnDestroy, OnInit, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { Subject } from 'rxjs';
import { takeUntil } from 'rxjs/operators';
import { LibraryApiService } from '../../../services/library-api.service';

interface CoverCandidate {
  url: string;
  width: number;
  height: number;
}

/**
 * Cover search (title/author → OpenLibrary + Google Books candidates, sorted
 * largest-first) plus a manual-URL fallback — the same widget
 * BookEditDialogComponent has always had inline, now shared so other edit
 * dialogs (currently the audiobook edit dialog) can offer it too. Search is
 * the primary way to pick a cover; the manual URL field is the fallback for
 * when search comes up empty. Only emits a selection — the parent decides
 * when/how to persist it (immediately vs. staged until Save).
 *
 * fetchLibraryCoverCandidates lives on LibraryApiService/under the
 * /api/library/... route for historical reasons, but the lookup itself
 * (title/author → cover images) isn't book-specific — safe to reuse here.
 */
@Component({
  selector: 'app-cover-picker',
  standalone: true,
  imports: [CommonModule, FormsModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatIconModule],
  template: `
    <button type="button" class="cover-button" (click)="togglePicker()" aria-label="Change cover">
      <img
        [src]="displayCoverUrl"
        [alt]="title + ' cover'"
        (error)="onCoverError($event)"
        class="picked-cover"
      />
      <span class="cover-edit-label">Change cover</span>
    </button>

    <div class="cover-picker" *ngIf="pickerOpen">
      <div class="cover-picker-header">
        <span>Available covers (sorted by size)</span>
        <div class="cover-picker-actions">
          <button mat-button type="button" (click)="openGoogleImages()">
            <mat-icon>search</mat-icon>
            Google Images
          </button>
          <button mat-button type="button" (click)="refreshCandidates()">Refresh</button>
        </div>
      </div>
      <div class="cover-picker-status" *ngIf="candidatesLoading">Loading covers...</div>
      <div class="cover-picker-status error" *ngIf="!candidatesLoading && candidatesError">
        {{ candidatesError }}
      </div>
      <div class="cover-grid" *ngIf="!candidatesLoading && candidates.length > 0">
        <button
          type="button"
          class="cover-option"
          *ngFor="let candidate of candidates"
          (click)="selectCover(candidate.url)"
          [class.selected]="candidate.url === selectedCoverUrl"
        >
          <img [src]="candidate.url" [alt]="title + ' cover option'" />
          <span class="cover-dim">{{ candidate.width }}x{{ candidate.height }}</span>
        </button>
      </div>

      <div class="manual-cover-row">
        <mat-form-field appearance="outline" class="w-100">
          <mat-label>Set your own cover image via URL</mat-label>
          <input matInput [(ngModel)]="manualCoverUrl" />
        </mat-form-field>
        <button mat-stroked-button type="button" (click)="applyManualCoverUrl()">Use URL</button>
      </div>
    </div>
  `,
  styles: [`
    .cover-button {
      position: relative;
      border: none;
      background: none;
      padding: 0;
      cursor: pointer;
      display: block;
    }

    .picked-cover {
      width: 150px;
      height: 225px;
      object-fit: cover;
      border-radius: 8px;
      box-shadow: 0 2px 8px rgba(0, 0, 0, 0.15);
      display: block;
    }

    .cover-edit-label {
      position: absolute;
      left: 6px;
      right: 6px;
      bottom: 6px;
      background: rgba(0, 0, 0, 0.7);
      color: #fff;
      font-size: 0.7rem;
      text-transform: uppercase;
      letter-spacing: 0.04em;
      padding: 4px 6px;
      border-radius: 6px;
      text-align: center;
    }

    .cover-picker {
      margin-top: 0.5rem;
    }

    .cover-picker-header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      font-size: 0.75rem;
      color: #6b6b6b;
      margin-bottom: 0.5rem;
    }

    .cover-picker-actions {
      display: flex;
      gap: 0.5rem;
      align-items: center;
    }

    .cover-picker-status {
      font-size: 0.8rem;
      color: #666;
      padding: 0.25rem 0;
    }

    .cover-picker-status.error {
      color: #b00020;
    }

    .cover-grid {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 0.5rem;
    }

    .cover-option {
      border: 2px solid transparent;
      background: #f7f7f7;
      border-radius: 8px;
      padding: 4px;
      cursor: pointer;
      transition: border-color 0.2s ease, box-shadow 0.2s ease;
    }

    .cover-option img {
      width: 100%;
      height: auto;
      display: block;
      border-radius: 6px;
    }

    .cover-option.selected {
      border-color: #3f51b5;
      box-shadow: 0 0 0 2px rgba(63, 81, 181, 0.2);
    }

    .cover-dim {
      display: block;
      text-align: center;
      font-size: 0.7rem;
      color: #555;
      margin-top: 0.25rem;
    }

    .w-100 {
      width: 100%;
    }

    .manual-cover-row {
      display: grid;
      grid-template-columns: 1fr auto;
      gap: 0.75rem;
      align-items: center;
      margin-top: 0.5rem;
    }
  `]
})
export class CoverPickerComponent implements OnInit, OnDestroy {
  @Input() title = '';
  @Input() author: string | null = null;
  @Input() currentCoverUrl: string | null = null;
  @Input() placeholderUrl = '/assets/placeholder.jpg';
  /** Opens the search panel (and kicks off a search) as soon as the picker
   * mounts, instead of waiting for a click on the preview thumbnail. */
  @Input() autoOpen = false;
  @Output() coverSelected = new EventEmitter<string>();

  selectedCoverUrl: string | null = null;
  pickerOpen = false;
  manualCoverUrl = '';
  candidates: CoverCandidate[] = [];
  candidatesLoading = false;
  candidatesError: string | null = null;

  private destroy$ = new Subject<void>();

  constructor(private libraryApi: LibraryApiService) {}

  ngOnInit(): void {
    if (this.autoOpen) {
      this.pickerOpen = true;
      this.loadCandidates();
    }
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  get displayCoverUrl(): string {
    return this.selectedCoverUrl || this.currentCoverUrl || this.placeholderUrl;
  }

  togglePicker(): void {
    this.pickerOpen = !this.pickerOpen;
    if (this.pickerOpen && this.candidates.length === 0 && !this.candidatesLoading) {
      this.loadCandidates();
    }
  }

  refreshCandidates(): void {
    this.candidates = [];
    this.loadCandidates();
  }

  selectCover(url: string): void {
    this.selectedCoverUrl = url;
    this.coverSelected.emit(url);
  }

  applyManualCoverUrl(): void {
    const trimmed = this.manualCoverUrl.trim();
    if (!trimmed) return;
    this.selectCover(trimmed);
    this.manualCoverUrl = '';
  }

  openGoogleImages(): void {
    const title = this.title?.trim() ?? '';
    let query = title;
    if (this.author) {
      query += ` ${this.author}`;
    }
    query += ' cover';
    const searchUrl = `https://www.google.com/search?tbm=isch&q=${encodeURIComponent(query)}`;
    window.open(searchUrl, '_blank');
  }

  onCoverError(evt: Event): void {
    const img = evt.target as HTMLImageElement;
    if (!img || img.src.endsWith(this.placeholderUrl)) {
      return;
    }
    img.src = this.placeholderUrl;
  }

  private loadCandidates(): void {
    const title = this.title?.trim();
    if (!title) {
      this.candidatesError = 'Missing title for cover lookup.';
      return;
    }

    this.candidatesLoading = true;
    this.candidatesError = null;

    this.libraryApi.fetchLibraryCoverCandidates(title, this.author ?? undefined).pipe(takeUntil(this.destroy$)).subscribe({
      next: (resp) => {
        const urls = Array.from(new Set(resp.covers || []));
        this.applyCandidates(urls)
          .catch(() => {
            this.candidatesError = 'Failed to load cover images.';
          })
          .finally(() => {
            if (this.candidates.length === 0 && !this.candidatesError) {
              this.candidatesError = 'No cover images found. Try Google Images or paste a URL manually.';
            }
            this.candidatesLoading = false;
          });
      },
      error: () => {
        this.candidatesLoading = false;
        this.candidatesError = 'Cover lookup failed. Try Google Images or paste a URL manually.';
      }
    });
  }

  private async applyCandidates(urls: string[]): Promise<void> {
    const validations = await Promise.all(urls.map((url) => this.validateCandidate(url)));
    const candidates = validations.filter((c): c is CoverCandidate => c !== null);
    // Sort by size (larger images first)
    this.candidates = candidates.sort((a, b) => (b.width * b.height) - (a.width * a.height));
  }

  private validateCandidate(url: string): Promise<CoverCandidate | null> {
    return new Promise((resolve) => {
      const img = new Image();
      img.onload = () => {
        const width = img.naturalWidth || img.width;
        const height = img.naturalHeight || img.height;
        if (!width || !height) {
          resolve(null);
          return;
        }
        resolve({ url, width, height });
      };
      img.onerror = () => resolve(null);
      img.src = url;
    });
  }
}
