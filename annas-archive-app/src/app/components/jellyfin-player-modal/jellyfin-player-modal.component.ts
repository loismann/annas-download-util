import { Component, ElementRef, Inject, OnDestroy, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MediaTrackInfo } from '../../services/media-library-api.service';

const PROGRESS_SAVE_INTERVAL_MS = 15000;

/** Minimal shape of the native (non-standard-in-TS-lib) AudioTrack/AudioTrackList
 *  APIs this component reads/writes — Chrome/Edge support switching which
 *  embedded audio track is active this way; Firefox/Safari support is spottier,
 *  hence the `'audioTracks' in video` feature-detect before ever touching it. */
interface NativeAudioTrack {
  label: string;
  language: string;
  enabled: boolean;
}
interface NativeAudioTrackList {
  length: number;
  [index: number]: NativeAudioTrack;
}

export interface JellyfinPlayerModalData {
  title: string;
  /** "embed": iframe into Jellyfin's own web player — used for anyone without
   *  personal Jellyfin credentials configured. "native": our own <video>
   *  element, streamed through this app so resume position, audio track, and
   *  subtitle selection can all be tracked against that person's own account. */
  mode: 'native' | 'embed';
  embedUrl?: string;
  streamUrl?: string;
  resumePositionSeconds?: number;
  audioTracks?: MediaTrackInfo[];
  subtitleTracks?: MediaTrackInfo[];
  /** Builds the WebVTT URL for one subtitle track's Jellyfin stream Index —
   *  a callback (not a plain field) so this component stays generic between
   *  movie and episode, which address subtitles differently server-side. */
  subtitleUrlFor?: (subtitleIndex: number) => string;
  /** Called periodically and on pause/close with the current position —
   *  same reasoning as subtitleUrlFor. */
  saveProgress?: (positionSeconds: number) => void;
}

/**
 * Plays a movie/episode two ways depending on data.mode:
 *  - "embed": the original iframe into Jellyfin's own web player (no auth
 *    passed in, no resume tracking — see MediaLibraryEndpoints.HandleWatchMovie).
 *  - "native": a plain <video> element we control, same hand-rolled pattern
 *    as AudiobookPlayerDialogComponent — resumes from data.resumePositionSeconds,
 *    reports position back via data.saveProgress, and offers pickers for
 *    embedded audio tracks (native browser AudioTrackList switching) and
 *    subtitle tracks (server-converted WebVTT <track> elements, since browsers
 *    can't parse embedded SRT/ASS/PGS out of a container themselves).
 */
@Component({
  selector: 'app-jellyfin-player-modal',
  standalone: true,
  imports: [CommonModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatSelectModule],
  templateUrl: './jellyfin-player-modal.component.html',
  styleUrl: './jellyfin-player-modal.component.css'
})
export class JellyfinPlayerModalComponent implements OnDestroy {
  @ViewChild('videoEl') videoElRef?: ElementRef<HTMLVideoElement>;

  safeEmbedUrl?: SafeResourceUrl;

  audioTrackOptions: { id: number; label: string }[] = [];
  selectedAudioTrackId: number | null = null;
  /** Index into data.subtitleTracks (and, 1:1, into video.textTracks — the
   *  <track> elements are rendered in that same order) — null means "Off". */
  selectedSubtitlePos: number | null = null;

  private progressSaveTimer?: ReturnType<typeof setInterval>;
  private lastSavedTime = 0;

  constructor(
    private dialogRef: MatDialogRef<JellyfinPlayerModalComponent>,
    private sanitizer: DomSanitizer,
    @Inject(MAT_DIALOG_DATA) public data: JellyfinPlayerModalData
  ) {
    if (data.mode === 'embed' && data.embedUrl) {
      // The embed URL is our own backend-resolved Jellyfin deep link (routed
      // through the CSP-stripping proxy) — trusted, not user-supplied.
      this.safeEmbedUrl = this.sanitizer.bypassSecurityTrustResourceUrl(data.embedUrl);
    }
  }

  ngOnDestroy(): void {
    if (this.progressSaveTimer) clearInterval(this.progressSaveTimer);
    this.saveProgress();
  }

  trackLabel(track: MediaTrackInfo): string {
    return track.title || track.language || `Track ${track.index}`;
  }

  subtitleUrl(track: MediaTrackInfo): string {
    return this.data.subtitleUrlFor?.(track.index) ?? '';
  }

  onLoadedMetadata(): void {
    const video = this.videoElRef?.nativeElement;
    if (!video || this.data.mode !== 'native') return;

    const resumeAt = this.data.resumePositionSeconds ?? 0;
    if (resumeAt > 0) video.currentTime = resumeAt;

    this.discoverAudioTracks(video);
    this.progressSaveTimer = setInterval(() => this.saveProgress(), PROGRESS_SAVE_INTERVAL_MS);
  }

  onPause(): void {
    this.saveProgress();
  }

  selectAudioTrack(id: number): void {
    const video = this.videoElRef?.nativeElement;
    const audioTracks = video && this.getAudioTrackList(video);
    if (!audioTracks) return;

    for (let i = 0; i < audioTracks.length; i++) {
      audioTracks[i].enabled = i === id;
    }
    this.selectedAudioTrackId = id;
  }

  selectSubtitle(pos: number | null): void {
    const video = this.videoElRef?.nativeElement;
    if (!video) return;

    for (let i = 0; i < video.textTracks.length; i++) {
      video.textTracks[i].mode = i === pos ? 'showing' : 'disabled';
    }
    this.selectedSubtitlePos = pos;
  }

  close(): void {
    this.dialogRef.close();
  }

  private discoverAudioTracks(video: HTMLVideoElement): void {
    const audioTracks = this.getAudioTrackList(video);
    if (!audioTracks || audioTracks.length <= 1) return; // nothing to switch between

    this.audioTrackOptions = [];
    let anyEnabled = false;
    for (let i = 0; i < audioTracks.length; i++) {
      const track = audioTracks[i];
      this.audioTrackOptions.push({ id: i, label: track.label || track.language || `Track ${i + 1}` });
      if (track.enabled) {
        this.selectedAudioTrackId = i;
        anyEnabled = true;
      }
    }

    // Observed on some multi-audio-track files: the browser's demuxer doesn't
    // auto-enable any track on its own, leaving playback silent until one is
    // explicitly enabled — force the first track on rather than trusting the
    // browser picked a default.
    if (!anyEnabled) this.selectAudioTrack(0);
  }

  private getAudioTrackList(video: HTMLVideoElement): NativeAudioTrackList | null {
    // Not part of the standard TS DOM lib — Chrome/Edge implement it, Firefox/
    // Safari support is inconsistent, hence the feature-detect rather than a
    // typed property access.
    return 'audioTracks' in video ? ((video as unknown as { audioTracks: NativeAudioTrackList }).audioTracks) : null;
  }

  private saveProgress(): void {
    const video = this.videoElRef?.nativeElement;
    if (!video || !this.data.saveProgress) return;

    const currentTime = video.currentTime;
    if (Math.abs(currentTime - this.lastSavedTime) < 1) return; // nothing meaningful to save

    this.lastSavedTime = currentTime;
    this.data.saveProgress(currentTime);
  }
}
