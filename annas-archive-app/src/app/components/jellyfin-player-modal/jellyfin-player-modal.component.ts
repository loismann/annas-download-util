import { AfterViewInit, Component, ElementRef, Inject, OnDestroy, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import Hls from 'hls.js';
import { MediaTrackInfo } from '../../services/media-library-api.service';

const PROGRESS_SAVE_INTERVAL_MS = 15000;
/** Below this, resuming isn't meaningfully different from starting over —
 *  skip the prompt and just play from the top. */
const MIN_RESUME_SECONDS = 15;
/** Within this many seconds of the end, treat a saved position as "already
 *  finished" rather than prompting to resume two minutes from the credits. */
const END_OF_MEDIA_BUFFER_SECONDS = 30;

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
  /** True when streamUrl points at this app's HLS master-playlist route rather
   *  than a plain file stream — Jellyfin transcoding a source file (AVI
   *  container, AC3/DTS audio, etc.) that no browser can decode natively.
   *  See MediaLibraryApiService.getMovieHlsMasterUrl. */
  isHls?: boolean;
  resumePositionSeconds?: number;
  durationSeconds?: number;
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
  styleUrl: './jellyfin-player-modal.component.scss'
})
export class JellyfinPlayerModalComponent implements AfterViewInit, OnDestroy {
  @ViewChild('videoEl') videoElRef?: ElementRef<HTMLVideoElement>;

  safeEmbedUrl?: SafeResourceUrl;

  audioTrackOptions: { id: number; label: string }[] = [];
  selectedAudioTrackId: number | null = null;
  /** Index into data.subtitleTracks (and, 1:1, into video.textTracks — the
   *  <track> elements are rendered in that same order) — null means "Off". */
  selectedSubtitlePos: number | null = null;

  /** True while the "Resume / Start Over" overlay is showing — playback is
   *  held until the user answers (see onLoadedMetadata/resumeFromSaved/
   *  restartFromBeginning). Only ever true in native mode; embed mode is
   *  Jellyfin's own player and handles its own resume UI. */
  resumeChoicePending = false;

  private progressSaveTimer?: ReturnType<typeof setInterval>;
  private lastSavedTime = 0;
  private hls?: Hls;
  private metadataLoaded = false;
  /** Set by resumeFromSaved/restartFromBeginning; applied immediately if
   *  metadata's already loaded, otherwise picked up by onLoadedMetadata —
   *  covers a click landing before loadedmetadata has fired, where setting
   *  currentTime wouldn't reliably stick yet. */
  private pendingChoice: 'resume' | 'restart' | null = null;

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

    if (data.mode === 'native') {
      const resumeAt = data.resumePositionSeconds ?? 0;
      const duration = data.durationSeconds;
      const alreadyFinished = duration != null && resumeAt > duration - END_OF_MEDIA_BUFFER_SECONDS;
      this.resumeChoicePending = resumeAt >= MIN_RESUME_SECONDS && !alreadyFinished;
    }
  }

  /** "23:14" / "1:03:22" for the resume prompt. */
  formattedResumeTime(): string {
    const total = Math.floor(this.data.resumePositionSeconds ?? 0);
    const hours = Math.floor(total / 3600);
    const minutes = Math.floor((total % 3600) / 60);
    const seconds = total % 60;
    const paddedSeconds = String(seconds).padStart(2, '0');
    return hours > 0 ? `${hours}:${String(minutes).padStart(2, '0')}:${paddedSeconds}` : `${minutes}:${paddedSeconds}`;
  }

  resumeFromSaved(): void {
    this.resumeChoicePending = false;
    if (this.metadataLoaded) this.applyChoice('resume');
    else this.pendingChoice = 'resume';
  }

  restartFromBeginning(): void {
    this.resumeChoicePending = false;
    if (this.metadataLoaded) this.applyChoice('restart');
    else this.pendingChoice = 'restart';
  }

  private applyChoice(choice: 'resume' | 'restart'): void {
    const video = this.videoElRef?.nativeElement;
    if (!video) return;
    video.currentTime = choice === 'resume' ? (this.data.resumePositionSeconds ?? 0) : 0;
    video.play();
  }

  ngAfterViewInit(): void {
    const video = this.videoElRef?.nativeElement;
    if (this.data.mode !== 'native' || !video || !this.data.streamUrl) return;

    if (!this.data.isHls) {
      video.src = this.data.streamUrl;
      return;
    }

    // Safari has native HLS support built into <video> itself; every other
    // browser needs hls.js's MediaSource-Extensions-based implementation.
    if (video.canPlayType('application/vnd.apple.mpegurl')) {
      video.src = this.data.streamUrl;
    } else if (Hls.isSupported()) {
      this.hls = new Hls();
      this.hls.loadSource(this.data.streamUrl);
      this.hls.attachMedia(video);

      // Without this, any fatal error (a segment fetch that 401s during a seek,
      // a transient network blip) just stops hls.js dead — the video freezes
      // with no attempt to recover. These are hls.js's own documented recovery
      // calls for each fatal error type; non-fatal errors are already retried
      // internally and don't need handling here.
      this.hls.on(Hls.Events.ERROR, (_event, data) => {
        if (!data.fatal || !this.hls) return;
        switch (data.type) {
          case Hls.ErrorTypes.NETWORK_ERROR:
            this.hls.startLoad();
            break;
          case Hls.ErrorTypes.MEDIA_ERROR:
            this.hls.recoverMediaError();
            break;
          default:
            this.hls.destroy();
            break;
        }
      });
    }
  }

  ngOnDestroy(): void {
    this.hls?.destroy();
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

    this.metadataLoaded = true;
    this.discoverAudioTracks(video);
    this.progressSaveTimer = setInterval(() => this.saveProgress(), PROGRESS_SAVE_INTERVAL_MS);

    if (this.pendingChoice) {
      this.applyChoice(this.pendingChoice);
      this.pendingChoice = null;
      return;
    }

    // Still waiting on the Resume/Start Over prompt — resumeFromSaved/
    // restartFromBeginning (via pendingChoice above) starts playback once
    // the user answers.
    if (this.resumeChoicePending) return;

    const resumeAt = this.data.resumePositionSeconds ?? 0;
    if (resumeAt > 0) video.currentTime = resumeAt;
    video.play();
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
