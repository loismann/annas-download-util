import { Component, ElementRef, HostListener, Inject, OnInit, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { NgxExtendedPdfViewerModule } from 'ngx-extended-pdf-viewer';
import { LibraryApiService } from '../../services/library-api.service';
import { LoggerService } from '../../services/logger.service';

export interface PdfViewerDialogData {
  title: string;
  fileName: string;
}

const LAST_PAGE_STORAGE_PREFIX = 'pdf-viewer-last-page:';

@Component({
  selector: 'app-pdf-viewer-dialog',
  standalone: true,
  imports: [
    CommonModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    NgxExtendedPdfViewerModule
  ],
  templateUrl: './pdf-viewer-dialog.component.html',
  styleUrls: ['./pdf-viewer-dialog.component.css']
})
export class PdfViewerDialogComponent implements OnInit {
  @ViewChild('pdfContainer') containerRef!: ElementRef<HTMLDivElement>;

  isLoading = true;
  hasError = false;
  pdfSrc: Blob | null = null;
  isFullscreen = false;

  /** Which page to open on — restored from the last page this book was left on,
   *  defaulting to page 1 for a book opened for the first time. */
  currentPage = 1;

  constructor(
    public dialogRef: MatDialogRef<PdfViewerDialogComponent>,
    @Inject(MAT_DIALOG_DATA) public data: PdfViewerDialogData,
    private libraryApi: LibraryApiService,
    private logger: LoggerService
  ) {}

  ngOnInit(): void {
    const storedPage = this.readStoredPage();
    if (storedPage) {
      this.currentPage = storedPage;
    }

    this.libraryApi.getLibraryBookFile(this.data.fileName).subscribe({
      next: blob => {
        this.pdfSrc = blob;
        this.isLoading = false;
      },
      error: err => {
        this.logger.error('Failed to load PDF file', err);
        this.hasError = true;
        this.isLoading = false;
      }
    });
  }

  @HostListener('document:keydown.escape', ['$event'])
  handleEscapeKey(event: KeyboardEvent): void {
    // While in fullscreen, let Escape exit fullscreen only (the browser already
    // does this natively) — closing the dialog on top of that would be a
    // surprising double-action for one keypress.
    if (document.fullscreenElement) return;
    event.preventDefault();
    this.onClose();
  }

  @HostListener('document:fullscreenchange')
  handleFullscreenChange(): void {
    this.isFullscreen = !!document.fullscreenElement;
  }

  toggleFullscreen(): void {
    if (document.fullscreenElement) {
      document.exitFullscreen();
    } else {
      this.containerRef?.nativeElement.requestFullscreen();
    }
  }

  onPageChange(page: number | undefined): void {
    if (!page) return;
    this.currentPage = page;
    try {
      localStorage.setItem(this.storageKey, String(page));
    } catch {
      // Storage can throw (private browsing, quota) — losing the resume
      // position isn't worth surfacing an error to the user over.
    }
  }

  onClose(): void {
    this.dialogRef.close();
  }

  private get storageKey(): string {
    return LAST_PAGE_STORAGE_PREFIX + this.data.fileName;
  }

  private readStoredPage(): number | null {
    try {
      const raw = localStorage.getItem(this.storageKey);
      const page = raw ? parseInt(raw, 10) : NaN;
      return Number.isFinite(page) && page > 0 ? page : null;
    } catch {
      return null;
    }
  }
}
