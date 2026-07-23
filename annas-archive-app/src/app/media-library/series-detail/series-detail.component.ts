import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { forkJoin } from 'rxjs';
import { MediaLibraryApiService, EpisodeInfo } from '../../services/media-library-api.service';
import { MediaSearchApiService, MediaLookupResult } from '../../services/media-search-api.service';
import {
  JellyfinPlayerModalComponent,
  JellyfinPlayerModalData
} from '../../components/jellyfin-player-modal/jellyfin-player-modal.component';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../components/confirm-dialog/confirm-dialog.component';
import { LoggerService } from '../../services/logger.service';

interface SeasonGroup {
  seasonNumber: number;
  label: string;
  episodes: EpisodeInfo[];
  /** True once every episode in the season already has a file — the
   * "nothing left to gain from downloading" state that disables the button. */
  allDownloaded: boolean;
}

@Component({
  selector: 'app-series-detail',
  standalone: true,
  imports: [
    CommonModule,
    MatProgressSpinnerModule,
    MatButtonModule,
    MatIconModule,
    MatTooltipModule,
    MatExpansionModule,
    MatDialogModule
  ],
  templateUrl: './series-detail.component.html',
  styleUrl: './series-detail.component.css'
})
export class SeriesDetailComponent implements OnInit {
  series: MediaLookupResult | null = null;
  seasonGroups: SeasonGroup[] = [];
  loading = true;
  error: string | null = null;
  resolvingEpisodeId: number | null = null;
  downloadingSeasonNumber: number | null = null;
  /** Seasons requested this visit — shown as "Requested" instead of
   * re-enabling "Download" immediately, since the file won't actually
   * exist until Sonarr finds and grabs it. */
  requestedSeasonNumbers = new Set<number>();

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private api: MediaLibraryApiService,
    private searchApi: MediaSearchApiService,
    private dialog: MatDialog,
    private logger: LoggerService
  ) {}

  ngOnInit(): void {
    const seriesId = Number(this.route.snapshot.paramMap.get('seriesId'));
    if (!seriesId) {
      this.error = 'Invalid series.';
      this.loading = false;
      return;
    }

    forkJoin({
      allSeries: this.api.getDownloadedTv(),
      episodes: this.api.getSeriesEpisodes(seriesId)
    }).subscribe({
      next: ({ allSeries, episodes }) => {
        this.series = allSeries.find(s => s.id === seriesId) || null;
        if (!this.series) {
          this.error = 'Series not found in your library.';
          this.loading = false;
          return;
        }

        const bySeason = new Map<number, EpisodeInfo[]>();
        for (const ep of episodes) {
          const list = bySeason.get(ep.seasonNumber) || [];
          list.push(ep);
          bySeason.set(ep.seasonNumber, list);
        }
        this.seasonGroups = [...bySeason.entries()]
          .sort(([a], [b]) => a - b)
          .map(([seasonNumber, eps]) => {
            const episodes = eps.sort((a, b) => a.episodeNumber - b.episodeNumber);
            return {
              seasonNumber,
              label: seasonNumber === 0 ? 'Specials' : `Season ${seasonNumber}`,
              episodes,
              allDownloaded: episodes.length > 0 && episodes.every(ep => ep.hasFile)
            };
          });

        this.loading = false;
      },
      error: (err) => {
        this.logger.error('[SeriesDetailComponent] load failed', err);
        this.error = 'Could not load this series — is Sonarr reachable?';
        this.loading = false;
      }
    });
  }

  goBack(): void {
    this.router.navigate(['/media-library']);
  }

  playEpisode(episode: EpisodeInfo): void {
    if (!this.series?.tvdbId || !episode.hasFile || this.resolvingEpisodeId !== null) return;

    this.resolvingEpisodeId = episode.id;
    this.api.watchTv(this.series.tvdbId, episode.seasonNumber, episode.episodeNumber).subscribe({
      next: (resp) => {
        this.resolvingEpisodeId = null;
        this.dialog.open<JellyfinPlayerModalComponent, JellyfinPlayerModalData>(JellyfinPlayerModalComponent, {
          width: '90vw',
          maxWidth: '1100px',
          data: { title: `${this.series!.title} — ${episode.title || 'S' + episode.seasonNumber + 'E' + episode.episodeNumber}`, embedUrl: resp.embedUrl }
        });
      },
      error: (err) => {
        this.resolvingEpisodeId = null;
        this.logger.error('[SeriesDetailComponent] watchTv failed', err);
        this.error = `Jellyfin hasn't matched this episode yet — it may still be scanning.`;
      }
    });
  }

  downloadSeason(group: SeasonGroup): void {
    if (!this.series?.id || group.allDownloaded || this.downloadingSeasonNumber !== null) return;

    this.downloadingSeasonNumber = group.seasonNumber;
    this.searchApi.updateTvSeasons(this.series.id, [group.seasonNumber]).subscribe({
      next: () => {
        this.downloadingSeasonNumber = null;
        this.requestedSeasonNumbers.add(group.seasonNumber);
      },
      error: (err) => {
        this.downloadingSeasonNumber = null;
        this.logger.error('[SeriesDetailComponent] downloadSeason failed', err);
        this.error = `Could not request ${group.label}.`;
      }
    });
  }

  deleteSeason(group: SeasonGroup): void {
    if (!this.series) return;
    const seriesId = this.series.id!;
    const seriesTitle = this.series.title;

    const dialogRef = this.dialog.open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
      width: '450px',
      data: {
        title: `Delete ${group.label}`,
        message: `Delete ${group.label} of "${seriesTitle}" and its downloaded files?\n\nThis cannot be undone.`,
        confirmText: 'Delete',
        isDanger: true
      }
    });

    dialogRef.afterClosed().subscribe(confirmed => {
      if (!confirmed) return;
      this.api.deleteSeason(seriesId, group.seasonNumber).subscribe({
        next: () => {
          this.seasonGroups = this.seasonGroups.filter(g => g !== group);
        },
        error: (err) => {
          this.logger.error('[SeriesDetailComponent] deleteSeason failed', err);
          this.error = `Could not delete ${group.label}.`;
        }
      });
    });
  }
}
