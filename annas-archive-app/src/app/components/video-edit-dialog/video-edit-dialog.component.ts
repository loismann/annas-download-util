import { Component, HostListener, Inject, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subject } from 'rxjs';
import { takeUntil } from 'rxjs/operators';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatDividerModule } from '@angular/material/divider';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatInputModule } from '@angular/material/input';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { COMMA, ENTER } from '@angular/cdk/keycodes';
import { MatChipInputEvent } from '@angular/material/chips';
import { VideoLibraryApiService } from '../../services/video-library-api.service';
import { LoggerService } from '../../services/logger.service';

export interface VideoEditDialogData {
  fileName: string;
  title: string;
  channel: string;
  duration: string;
  resolution: string | null;
  format: string;
  fileSize: string;
  thumbnailUrl: string | null;
  description: string | null;
  primaryGenre: string | null;
  tags: string[];
  playlist: string | null;
  youTubeId: string | null;
  availableGenres?: string[];
}

export interface VideoEditDialogResult {
  primaryGenre?: string | null;
  tags?: string[];
  playlist?: string | null;
  title?: string;
  channel?: string;
  deleted?: boolean;
}

@Component({
  selector: 'app-video-edit-dialog',
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
    MatProgressSpinnerModule,
    MatDividerModule
  ],
  templateUrl: './video-edit-dialog.component.html',
  styleUrls: ['./video-edit-dialog.component.css']
})
export class VideoEditDialogComponent implements OnDestroy {
  private destroy$ = new Subject<void>();

  readonly separatorKeysCodes = [ENTER, COMMA] as const;

  genres: string[];
  tags: string[];
  playlist: string | null;
  title: string;
  channel: string;
  placeholderUrl = '/assets/video-placeholder.jpg';
  isDeleting = false;
  deleteConfirmPending = false;
  private deleteConfirmTimeout: ReturnType<typeof setTimeout> | null = null;

  constructor(
    public dialogRef: MatDialogRef<VideoEditDialogComponent>,
    @Inject(MAT_DIALOG_DATA) public data: VideoEditDialogData,
    private videoApi: VideoLibraryApiService,
    private logger: LoggerService
  ) {
    const fromLibrary = (data.availableGenres ?? []).filter(Boolean);
    this.genres = fromLibrary.length > 0 ? [...fromLibrary] : [];
    if (!this.genres.includes('Uncategorized')) {
      this.genres.unshift('Uncategorized');
    }

    this.tags = [...(data.tags || [])];
    this.playlist = data.playlist;
    this.title = data.title;
    this.channel = data.channel;
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
    if (this.deleteConfirmTimeout) {
      clearTimeout(this.deleteConfirmTimeout);
    }
  }

  @HostListener('document:keydown.enter', ['$event'])
  handleEnterKey(event: KeyboardEvent): void {
    if (this.isTagInput(event.target as HTMLElement)) {
      return;
    }
    event.preventDefault();

    if (this.deleteConfirmPending) {
      this.executeDelete();
      return;
    }

    this.onSave();
  }

  @HostListener('document:keydown.escape', ['$event'])
  handleEscapeKey(event: KeyboardEvent): void {
    event.preventDefault();

    if (this.deleteConfirmPending) {
      this.cancelDeleteConfirm();
      return;
    }

    this.onCancel();
  }

  @HostListener('document:keydown.delete', ['$event'])
  handleDeleteKey(event: KeyboardEvent): void {
    if (this.isInputElement(event.target as HTMLElement)) {
      return;
    }
    event.preventDefault();
    this.initiateDeleteConfirm();
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
    const genresLower = this.genres.map(g => g.toLowerCase());
    const primaryGenre = this.tags.find(tag =>
      genresLower.includes(tag.toLowerCase()) && tag !== 'Uncategorized'
    ) || null;

    this.dialogRef.close({
      primaryGenre,
      tags: this.tags,
      playlist: this.playlist,
      title: this.title.trim() || this.data.title,
      channel: this.channel.trim() || this.data.channel
    } as VideoEditDialogResult);
  }

  onCancel(): void {
    this.dialogRef.close();
  }

  onThumbnailError(evt: Event): void {
    const img = evt.target as HTMLImageElement;
    if (!img || img.src.endsWith(this.placeholderUrl)) {
      return;
    }
    img.src = this.placeholderUrl;
  }

  get displayThumbnailUrl(): string {
    return this.data.thumbnailUrl || this.placeholderUrl;
  }

  get availableGenres(): string[] {
    const tagsLower = this.tags.map(t => t.toLowerCase());
    return this.genres.filter(g =>
      g !== 'Uncategorized' && !tagsLower.includes(g.toLowerCase())
    );
  }

  onGenreSelected(value: string | null): void {
    if (!value) return;
    this.addTagValue(value);
  }

  confirmDelete(): void {
    if (this.isDeleting || !this.data.fileName) {
      return;
    }

    const ok = window.confirm(`Delete "${this.data.title}" from the library? This cannot be undone.`);
    if (!ok) return;

    this.executeDelete();
  }

  initiateDeleteConfirm(): void {
    if (this.isDeleting || !this.data.fileName || this.deleteConfirmPending) {
      return;
    }

    this.deleteConfirmPending = true;

    if (this.deleteConfirmTimeout) {
      clearTimeout(this.deleteConfirmTimeout);
    }
    this.deleteConfirmTimeout = setTimeout(() => {
      this.cancelDeleteConfirm();
    }, 5000);
  }

  cancelDeleteConfirm(): void {
    this.deleteConfirmPending = false;
    if (this.deleteConfirmTimeout) {
      clearTimeout(this.deleteConfirmTimeout);
      this.deleteConfirmTimeout = null;
    }
  }

  private executeDelete(): void {
    if (this.isDeleting || !this.data.fileName) {
      return;
    }

    this.cancelDeleteConfirm();
    this.isDeleting = true;

    this.videoApi.deleteVideo(this.data.fileName).pipe(takeUntil(this.destroy$)).subscribe({
      next: () => {
        this.dialogRef.close({ deleted: true } as VideoEditDialogResult);
      },
      error: () => {
        this.isDeleting = false;
      }
    });
  }

  openOnYouTube(): void {
    if (this.data.youTubeId) {
      window.open(`https://www.youtube.com/watch?v=${this.data.youTubeId}`, '_blank');
    }
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

  private isInputElement(target: HTMLElement | null): boolean {
    if (!target) return false;
    return target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || !!target.closest('mat-chip-grid');
  }
}
