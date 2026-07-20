import { Component, Inject, HostListener, OnDestroy, AfterViewInit, ViewChild, ElementRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

export interface VideoPlayerDialogData {
  title: string;
  channel: string;
  streamUrl: string;
  youTubeId: string | null;
}

@Component({
  selector: 'app-video-player-dialog',
  standalone: true,
  imports: [
    CommonModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule
  ],
  templateUrl: './video-player-dialog.component.html',
  styleUrls: ['./video-player-dialog.component.css']
})
export class VideoPlayerDialogComponent implements AfterViewInit, OnDestroy {
  @ViewChild('videoPlayer') videoPlayerRef!: ElementRef<HTMLVideoElement>;

  isLoading = true;
  hasError = false;
  errorMessage = '';

  constructor(
    public dialogRef: MatDialogRef<VideoPlayerDialogComponent>,
    @Inject(MAT_DIALOG_DATA) public data: VideoPlayerDialogData
  ) {}

  ngAfterViewInit(): void {
    const video = this.videoPlayerRef?.nativeElement;
    if (video) {
      video.addEventListener('loadeddata', () => {
        this.isLoading = false;
      });
      video.addEventListener('error', (e) => {
        this.isLoading = false;
        this.hasError = true;
        this.errorMessage = 'Failed to load video. The format may not be supported by your browser.';
      });
      video.addEventListener('canplay', () => {
        this.isLoading = false;
      });
    }
  }

  ngOnDestroy(): void {
    // Pause video when dialog closes
    const video = this.videoPlayerRef?.nativeElement;
    if (video) {
      video.pause();
    }
  }

  @HostListener('document:keydown.escape', ['$event'])
  handleEscapeKey(event: KeyboardEvent): void {
    event.preventDefault();
    this.onClose();
  }

  @HostListener('document:keydown.space', ['$event'])
  handleSpaceKey(event: KeyboardEvent): void {
    // Only toggle play/pause if not focused on a button
    if ((event.target as HTMLElement).tagName === 'BUTTON') {
      return;
    }
    event.preventDefault();
    this.togglePlayPause();
  }

  onClose(): void {
    this.dialogRef.close();
  }

  togglePlayPause(): void {
    const video = this.videoPlayerRef?.nativeElement;
    if (!video) return;

    if (video.paused) {
      video.play();
    } else {
      video.pause();
    }
  }

  openOnYouTube(): void {
    if (this.data.youTubeId) {
      window.open(`https://www.youtube.com/watch?v=${this.data.youTubeId}`, '_blank');
    }
  }

  toggleFullscreen(): void {
    const video = this.videoPlayerRef?.nativeElement;
    if (!video) return;

    if (document.fullscreenElement) {
      document.exitFullscreen();
    } else {
      video.requestFullscreen();
    }
  }
}
