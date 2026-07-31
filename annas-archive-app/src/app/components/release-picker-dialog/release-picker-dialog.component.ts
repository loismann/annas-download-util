import { Component, Inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { Observable } from 'rxjs';
import { ReleaseInfo } from '../../services/media-library-api.service';
import { LoggerService } from '../../services/logger.service';

export interface ReleasePickerDialogData {
  title: string;
  fetch: () => Observable<ReleaseInfo[]>;
  grab: (release: ReleaseInfo) => Observable<void>;
}

function formatBytes(bytes: number): string {
  if (!bytes || bytes <= 0) return 'unknown size';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let size = bytes;
  let unitIndex = 0;
  while (size >= 1024 && unitIndex < units.length - 1) {
    size /= 1024;
    unitIndex++;
  }
  return `${size.toFixed(1)} ${units[unitIndex]}`;
}

/**
 * Radarr/Sonarr auto-reject releases that don't fit the quality profile
 * (e.g. too large under a size-capped profile) and never surface them —
 * this dialog runs the same interactive search Radarr/Sonarr's own UI uses,
 * but shows every result including the rejected ones, with the reason why,
 * so the user can make the size-vs-availability call themselves instead of
 * a show/movie sitting "missing" forever with no visibility into why.
 */
@Component({
  selector: 'app-release-picker-dialog',
  standalone: true,
  imports: [
    CommonModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatTooltipModule,
    MatProgressSpinnerModule
  ],
  template: `
    <div class="release-picker-dialog">
      <h2 mat-dialog-title>{{ data.title }}</h2>

      <div mat-dialog-content>
        <div *ngIf="loading" class="loading">
          <mat-spinner diameter="32"></mat-spinner>
          <span>Searching indexers…</span>
        </div>

        <div *ngIf="error" class="error">{{ error }}</div>

        <div *ngIf="!loading && !error && releases.length === 0" class="empty-state">
          No releases found. Your indexers may not have anything for this yet — try again later.
        </div>

        <div *ngIf="!loading && !error && releases.length > 0" class="release-list">
          <div class="release-row" *ngFor="let release of releases" [class.rejected]="release.rejected">
            <div class="release-main">
              <div class="release-title" [title]="release.title">{{ release.title }}</div>
              <div class="release-meta">
                <span class="release-size">{{ formatSize(release.size) }}</span>
                <span *ngIf="release.quality?.quality?.name">· {{ release.quality!.quality!.name }}</span>
                <span *ngIf="release.protocol === 'torrent' && release.seeders !== undefined">
                  · {{ release.seeders }} seeders
                </span>
                <span *ngIf="release.protocol === 'usenet' && release.ageHours !== undefined">
                  · {{ (release.ageHours / 24) | number: '1.0-0' }}d old
                </span>
                <span *ngIf="release.indexer"> · {{ release.indexer }}</span>
              </div>
              <div
                *ngIf="release.rejected"
                class="rejected-badge"
                [matTooltip]="(release.rejections || []).join('\\n')"
              >
                <mat-icon>warning</mat-icon>
                <span>{{ rejectionSummary(release) }}</span>
              </div>
            </div>
            <button
              mat-raised-button
              color="primary"
              [disabled]="grabbingGuid !== null"
              (click)="grab(release)"
            >
              <mat-icon *ngIf="grabbingGuid === release.guid">hourglass_empty</mat-icon>
              {{ grabbingGuid === release.guid ? 'Grabbing…' : 'Grab' }}
            </button>
          </div>
        </div>
      </div>

      <div mat-dialog-actions align="end">
        <button mat-stroked-button (click)="dialogRef.close(false)">Close</button>
      </div>
    </div>
  `,
  styles: [`
    .release-picker-dialog { min-width: min(520px, calc(100vw - 80px)); max-width: 640px; }
    .loading {
      display: flex;
      align-items: center;
      gap: 12px;
      padding: 1.5rem 0;
      color: #64748b;
    }
    .error { color: #f44336; padding: 1rem 0; overflow-wrap: anywhere; }
    .empty-state { color: #64748b; padding: 1rem 0; }
    .release-list {
      max-height: 50vh;
      overflow-y: auto;
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .release-row {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      padding: 10px 12px;
      border: 1px solid #e5e7eb;
      border-radius: 8px;
    }
    .release-row.rejected {
      background: #fff7ed;
      border-color: #fed7aa;
    }
    .release-main { min-width: 0; flex: 1 1 auto; }
    .release-title {
      font-size: 0.9rem;
      font-weight: 500;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .release-meta {
      font-size: 0.78rem;
      color: #64748b;
      margin-top: 2px;
    }
    .rejected-badge {
      display: flex;
      align-items: center;
      gap: 4px;
      font-size: 0.75rem;
      color: #c2410c;
      margin-top: 4px;
      cursor: help;
    }
    .rejected-badge mat-icon {
      flex: 0 0 auto;
      font-size: 16px;
      width: 16px;
      height: 16px;
    }
    .rejected-badge span { overflow-wrap: anywhere; }
  `]
})
export class ReleasePickerDialogComponent {
  loading = true;
  error: string | null = null;
  releases: ReleaseInfo[] = [];
  grabbingGuid: string | null = null;

  constructor(
    public dialogRef: MatDialogRef<ReleasePickerDialogComponent, boolean>,
    @Inject(MAT_DIALOG_DATA) public data: ReleasePickerDialogData,
    private logger: LoggerService
  ) {
    data.fetch().subscribe({
      next: (releases) => {
        this.releases = [...releases].sort((a, b) => (a.size ?? 0) - (b.size ?? 0));
        this.loading = false;
      },
      error: (err) => {
        this.logger.error('[ReleasePickerDialogComponent] fetch failed', err);
        this.error = 'Could not search for releases — is Radarr/Sonarr reachable?';
        this.loading = false;
      }
    });
  }

  formatSize(bytes: number): string {
    return formatBytes(bytes);
  }

  rejectionSummary(release: ReleaseInfo): string {
    const reasons = (release.rejections || []).filter(reason => reason.trim().length > 0);
    if (reasons.length === 0) return 'Would normally be skipped';
    return reasons.length === 1 ? reasons[0] : `${reasons[0]} (+${reasons.length - 1} more)`;
  }

  grab(release: ReleaseInfo): void {
    if (this.grabbingGuid !== null) return;
    this.grabbingGuid = release.guid;
    this.data.grab(release).subscribe({
      next: () => this.dialogRef.close(true),
      error: (err) => {
        this.grabbingGuid = null;
        this.logger.error('[ReleasePickerDialogComponent] grab failed', err);
        const detail: string | undefined = err?.error?.error;
        this.error = detail
          ? `Could not grab "${release.title}": ${detail}`
          : `Could not grab "${release.title}".`;
      }
    });
  }
}
