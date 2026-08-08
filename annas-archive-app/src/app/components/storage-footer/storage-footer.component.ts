import { Component, Input, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Subscription, interval, startWith, switchMap } from 'rxjs';
import { SystemStatsApiService, StorageStats } from '../../services/system-stats-api.service';
import { LoggerService } from '../../services/logger.service';

const MIB = 1024 ** 2;
const GIB = 1024 ** 3;
const TIB = 1024 ** 4;
// The backend itself caches this for 10 minutes (it's not cheap to compute —
// a full library directory scan plus Sonarr/Radarr calls) — refreshing more
// often than that here would just re-fetch the same cached value.
const REFRESH_MS = 10 * 60 * 1000;

@Component({
  selector: 'app-storage-footer',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './storage-footer.component.html',
  styleUrl: './storage-footer.component.scss'
})
export class StorageFooterComponent implements OnInit, OnDestroy {
  /** Matches the sidebar to the Date Night pages' black background. */
  @Input() dark = false;

  stats: StorageStats | null = null;
  private sub?: Subscription;

  constructor(
    private api: SystemStatsApiService,
    private logger: LoggerService
  ) {}

  ngOnInit(): void {
    this.sub = interval(REFRESH_MS)
      .pipe(
        startWith(0),
        switchMap(() => this.api.getStorageStats())
      )
      .subscribe({
        next: (stats) => (this.stats = stats),
        error: (err) => this.logger.error('[StorageFooterComponent] failed to load stats', err)
      });
  }

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
  }

  /**
   * Scales the unit to the value. Everything was TB when the only categories
   * were Movies/TV/Books, which are all multi-terabyte; Photos starts life at
   * well under a gigabyte and would read as a flat "0.00 TB" forever.
   */
  formatSize(bytes: number): string {
    if (bytes >= TIB) return `${(bytes / TIB).toFixed(2)} TB`;
    if (bytes >= GIB) return `${(bytes / GIB).toFixed(1)} GB`;
    if (bytes > 0) return `${Math.max(1, Math.round(bytes / MIB))} MB`;
    return '—';
  }
}
