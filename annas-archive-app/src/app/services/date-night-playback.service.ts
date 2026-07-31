import { Injectable } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { Observable, map, switchMap, tap, throwError } from 'rxjs';
import { JellyfinPlayerModalComponent, JellyfinPlayerModalData } from '../components/jellyfin-player-modal/jellyfin-player-modal.component';
import { DateNightApiService } from './date-night-api.service';
import { MediaLibraryApiService } from './media-library-api.service';

/** Shared Play action for the fullscreen countdown and the Date Night page.
 * The backend records the start first (enforcing T through T+1 hour), then the
 * existing Jellyfin watch/player flow launches from the same user gesture. */
@Injectable({ providedIn: 'root' })
export class DateNightPlaybackService {
  constructor(
    private dateNightApi: DateNightApiService,
    private mediaApi: MediaLibraryApiService,
    private dialog: MatDialog
  ) {}

  play(title: string, tmdbId?: number): Observable<void> {
    if (tmdbId == null) {
      return throwError(() => new Error('This movie has no TMDB id for Jellyfin playback.'));
    }

    return this.dateNightApi.startShowtime().pipe(
      switchMap(() => this.mediaApi.watchMovie(tmdbId)),
      tap(resp => {
        this.dialog.open<JellyfinPlayerModalComponent, JellyfinPlayerModalData>(JellyfinPlayerModalComponent, {
          width: '90vw',
          maxWidth: '1100px',
          data: {
            title,
            mode: resp.mode,
            embedUrl: resp.embedUrl,
            streamUrl: resp.mode === 'native'
              ? (resp.playbackMode === 'transcode'
                ? this.mediaApi.getMovieHlsMasterUrl(tmdbId)
                : this.mediaApi.getMovieStreamUrl(tmdbId))
              : undefined,
            isHls: resp.playbackMode === 'transcode',
            resumePositionSeconds: resp.resumePositionSeconds,
            durationSeconds: resp.durationSeconds,
            audioTracks: resp.audioTracks,
            subtitleTracks: resp.subtitleTracks,
            subtitleUrlFor: resp.mode === 'native' && resp.mediaSourceId
              ? subtitleIndex => this.mediaApi.getMovieSubtitleUrl(tmdbId, resp.mediaSourceId!, subtitleIndex)
              : undefined,
            saveProgress: resp.mode === 'native'
              ? positionSeconds => this.mediaApi.saveMovieProgress(tmdbId, positionSeconds).subscribe()
              : undefined
          }
        });
      }),
      map(() => undefined)
    );
  }
}
