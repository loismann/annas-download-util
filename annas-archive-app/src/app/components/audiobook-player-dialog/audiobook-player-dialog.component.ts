import { AfterViewInit, Component, ElementRef, Inject, OnDestroy, OnInit, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatSelectModule } from '@angular/material/select';
import { MatFormFieldModule } from '@angular/material/form-field';
import { FormsModule } from '@angular/forms';
import { AudiobookApiService, AudiobookChapter, AudiobookItem } from '../../services/audiobook-api.service';
import { AuthService } from '../../services/auth.service';
import { LoggerService } from '../../services/logger.service';

export interface AudiobookPlayerDialogData {
  item: AudiobookItem;
}

const PLACEHOLDER_COVER = '/assets/placeholder.jpg';
const PROGRESS_SAVE_INTERVAL_MS = 15000;
const PLAYBACK_RATES = [0.75, 1, 1.25, 1.5, 1.75, 2];

function formatTime(seconds: number): string {
  if (!isFinite(seconds) || seconds < 0) seconds = 0;
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = Math.floor(seconds % 60);
  const mm = m.toString().padStart(h > 0 ? 2 : 1, '0');
  const ss = s.toString().padStart(2, '0');
  return h > 0 ? `${h}:${mm}:${ss}` : `${mm}:${ss}`;
}

/**
 * Custom in-app audio player — the audiobook equivalent of the app's
 * existing custom EPUB reader (built in-house rather than embedding
 * Audiobookshelf's own web player), so favorite/cover/chapter UI stays
 * consistent with the rest of the app. Audio is simple enough to hand-roll
 * (a plain <audio> element + Range-request seeking) that this doesn't carry
 * the risk that justified using Jellyfin's own player for video.
 *
 * Handles multi-file audiobooks (item.audioFiles.length > 1) by tracking a
 * cumulative time offset per file and switching the <audio> element's src
 * when playback crosses a file boundary — chapters/progress are always
 * expressed in this global (whole-book) time, matching how Audiobookshelf
 * itself represents chapters for multi-file items.
 */
@Component({
  selector: 'app-audiobook-player-dialog',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatDialogModule,
    MatIconModule,
    MatButtonModule,
    MatSelectModule,
    MatFormFieldModule
  ],
  templateUrl: './audiobook-player-dialog.component.html',
  styleUrl: './audiobook-player-dialog.component.css'
})
export class AudiobookPlayerDialogComponent implements OnInit, AfterViewInit, OnDestroy {
  @ViewChild('audioEl') audioElRef!: ElementRef<HTMLAudioElement>;

  readonly rates = PLAYBACK_RATES;

  currentFileIndex = 0;
  /** Global (whole-book) playback position, in seconds. */
  globalTime = 0;
  totalDuration = 0;
  playing = false;
  playbackRate = 1;
  loadError: string | null = null;

  private fileOffsets: number[] = [];
  private progressSaveTimer?: ReturnType<typeof setInterval>;
  private pendingSeekOnLoad: number | null = null;
  private lastSavedTime = 0;

  constructor(
    public dialogRef: MatDialogRef<AudiobookPlayerDialogComponent>,
    @Inject(MAT_DIALOG_DATA) public data: AudiobookPlayerDialogData,
    private api: AudiobookApiService,
    private authService: AuthService,
    private logger: LoggerService
  ) {}

  ngOnInit(): void {
    const files = this.data.item.media?.audioFiles ?? [];
    let offset = 0;
    this.fileOffsets = files.map(f => {
      const start = offset;
      offset += f.duration ?? 0;
      return start;
    });
    this.totalDuration = this.data.item.media?.duration ?? offset;

    const ownerName = this.authService.getOwnerName();
    const savedPosition = ownerName ? this.data.item.progress?.[ownerName] : undefined;
    if (savedPosition && savedPosition > 0) {
      this.pendingSeekOnLoad = savedPosition;
    }

    this.progressSaveTimer = setInterval(() => this.saveProgress(), PROGRESS_SAVE_INTERVAL_MS);
  }

  ngAfterViewInit(): void {
    this.loadFile(this.resolveFileIndexForTime(this.pendingSeekOnLoad ?? 0));
  }

  ngOnDestroy(): void {
    if (this.progressSaveTimer) clearInterval(this.progressSaveTimer);
    this.saveProgress();
  }

  get chapters(): AudiobookChapter[] {
    return this.data.item.media?.chapters ?? [];
  }

  get currentChapter(): AudiobookChapter | undefined {
    return this.chapters.find(c => this.globalTime >= c.start && this.globalTime < c.end);
  }

  get title(): string {
    return this.data.item.media?.metadata?.title ?? 'Untitled';
  }

  get author(): string | undefined {
    return this.data.item.media?.metadata?.authorName || undefined;
  }

  get narrator(): string | undefined {
    return this.data.item.media?.metadata?.narratorName || undefined;
  }

  get coverUrl(): string {
    return this.data.item.id && this.data.item.media?.coverPath ? this.api.getCoverUrl(this.data.item.id) : PLACEHOLDER_COVER;
  }

  get isFavorited(): boolean {
    const ownerName = this.authService.getOwnerName();
    return !!ownerName && (this.data.item.favorites ?? []).includes(ownerName);
  }

  get formattedCurrentTime(): string {
    return formatTime(this.globalTime);
  }

  get formattedTotalDuration(): string {
    return formatTime(this.totalDuration);
  }

  togglePlay(): void {
    const audio = this.audioElRef?.nativeElement;
    if (!audio) return;
    if (audio.paused) {
      audio.play();
    } else {
      audio.pause();
    }
  }

  onPlay(): void {
    this.playing = true;
  }

  onPause(): void {
    this.playing = false;
    this.saveProgress();
  }

  onTimeUpdate(): void {
    const audio = this.audioElRef?.nativeElement;
    if (!audio) return;
    this.globalTime = this.fileOffsets[this.currentFileIndex] + audio.currentTime;
  }

  onEnded(): void {
    const files = this.data.item.media?.audioFiles ?? [];
    if (this.currentFileIndex < files.length - 1) {
      this.loadFile(this.currentFileIndex + 1, /* autoplay */ true);
    } else {
      this.playing = false;
      this.saveProgress();
    }
  }

  onLoadError(): void {
    this.loadError = 'Could not load audio — Audiobookshelf may be unreachable.';
  }

  /** Scrub bar drag — value is the global (whole-book) time in seconds. */
  onSeek(globalSeconds: number): void {
    const targetFileIndex = this.resolveFileIndexForTime(globalSeconds);
    const localTime = globalSeconds - this.fileOffsets[targetFileIndex];

    if (targetFileIndex === this.currentFileIndex) {
      const audio = this.audioElRef?.nativeElement;
      if (audio) audio.currentTime = localTime;
    } else {
      this.loadFile(targetFileIndex, this.playing, localTime);
    }
    this.globalTime = globalSeconds;
  }

  jumpToChapter(chapter: AudiobookChapter): void {
    this.onSeek(chapter.start);
  }

  onRateChange(rate: number): void {
    this.playbackRate = rate;
    const audio = this.audioElRef?.nativeElement;
    if (audio) audio.playbackRate = rate;
  }

  toggleFavorite(): void {
    const ownerName = this.authService.getOwnerName();
    if (!ownerName || !this.data.item.id) return;

    const wasFavorited = this.isFavorited;
    const newValue = !wasFavorited;

    this.data.item.favorites = newValue
      ? [...(this.data.item.favorites ?? []), ownerName]
      : (this.data.item.favorites ?? []).filter(o => o !== ownerName);

    this.api.setFavorite(this.data.item.id, newValue).subscribe({
      next: (resp) => {
        this.data.item.favorites = resp.favorites;
      },
      error: (err) => {
        this.logger.error('[AudiobookPlayerDialog] toggleFavorite failed', err);
        this.data.item.favorites = wasFavorited
          ? [...(this.data.item.favorites ?? []), ownerName]
          : (this.data.item.favorites ?? []).filter(o => o !== ownerName);
      }
    });
  }

  close(): void {
    this.dialogRef.close();
  }

  private resolveFileIndexForTime(globalSeconds: number): number {
    for (let i = this.fileOffsets.length - 1; i >= 0; i--) {
      if (globalSeconds >= this.fileOffsets[i]) return i;
    }
    return 0;
  }

  private loadFile(index: number, autoplay = false, localStartTime = 0): void {
    const files = this.data.item.media?.audioFiles ?? [];
    const file = files[index];
    if (!file || !this.data.item.id) return;

    this.currentFileIndex = index;
    const audio = this.audioElRef?.nativeElement;
    if (!audio) return;

    audio.src = this.api.getStreamUrl(this.data.item.id, file.ino);
    audio.playbackRate = this.playbackRate;

    const onReady = () => {
      audio.currentTime = localStartTime;
      if (autoplay) audio.play();
      audio.removeEventListener('loadedmetadata', onReady);
    };
    audio.addEventListener('loadedmetadata', onReady);
  }

  private saveProgress(): void {
    if (!this.data.item.id) return;
    if (Math.abs(this.globalTime - this.lastSavedTime) < 1) return; // nothing meaningful to save

    this.lastSavedTime = this.globalTime;
    this.api.saveProgress(this.data.item.id, this.globalTime).subscribe({
      error: (err) => this.logger.error('[AudiobookPlayerDialog] saveProgress failed', err)
    });
  }
}
